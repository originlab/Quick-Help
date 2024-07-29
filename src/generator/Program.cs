using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Xml.Linq;
using AngleSharp.Dom;
using AngleSharp.Html;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Html;
using OriginLab.DocumentGeneration.Templates;
using Razor.Templating.Core;

namespace OriginLab.DocumentGeneration;

internal class Program
{
    private const string RootUrlPrefix = "/static";
    private readonly Options Options;
    private readonly ImmutableArray<string> Languages;
    private readonly FrozenDictionary<string, (string url, string title)> PageInfo;

    static async Task<int> Main(string[] args)
    {
        var parsed = CommandLine.Parser.Default.ParseArguments<Options>(args);

        if (parsed.Tag != CommandLine.ParserResultType.Parsed)
        {
            foreach (var error in parsed.Errors)
            {
                Console.Error.WriteLine(error);
            }

            return -1;
        }

        try
        {
            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (o, ev) =>
            {
                cts.Cancel();
                ev.Cancel = true;
            };

            var program = new Program(parsed.Value);

            await program.BuildOutput(cts.Token);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);

            return -2;
        }

        return 0;
    }

    public Program(Options options)
    {
        if (!Directory.Exists(options.SourcePath))
        {
            throw new FileNotFoundException("SourcePath does not exist.", options.SourcePath);
        }

        Languages = (from fullPath in Directory.EnumerateDirectories(options.SourcePath)
                     let folderName = Path.GetFileName(fullPath)
                     where folderName.Length == 2
                     select folderName).ToImmutableArray();

        if (!Languages.Contains("en"))
        {
            throw new FileNotFoundException("SourcePath is not a valid directory.", options.SourcePath);
        }

        Options = options;
        PageInfo = GetPageInfo();
    }

    private async Task BuildOutput(CancellationToken cancellation)
    {
        if (Directory.Exists(Options.OutputPath))
        {
            Directory.Delete(Options.OutputPath, true);
        }

        Directory.CreateDirectory(Options.OutputPath);

        foreach (var language in Languages)
        {
            Directory.CreateDirectory(Path.Combine(Options.OutputPath, language));

            if (!Options.IndexPagesOnly)
            {
                foreach (var bookFolder in Directory.EnumerateDirectories(Path.Combine(Options.SourcePath, "en")))
                {
                    var pages = GetPagesInfo(GetPagesXml(Path.Combine(bookFolder, "book.xml"))).ToList();
                    var bookUrlName = pages[0].url;
                    var bookFolderName = Path.GetFileName(bookFolder);

                    CopyImages(Options.OutputPath, language, bookFolderName, bookUrlName);

                    await Parallel.ForEachAsync(pages,
                    new ParallelOptions
                    {
#if DEBUG
                        MaxDegreeOfParallelism = 1,
#endif
                        CancellationToken = cancellation,
                    },
                    async (page, cancellation) =>
                    {
                        var pageFolder = Path.Combine(Options.OutputPath, language, page.url);

                        Directory.CreateDirectory(pageFolder);

                        await ProcessPage(bookUrlName, bookFolderName, page, language, Path.Combine(pageFolder, "index.html"));
                    });
                }
            }

            await ProcessPage("", "", ("Index", "index.html", ""), language, Path.Combine(Options.OutputPath, language, "index.html"));
        }

        File.Copy(Path.Combine(Options.OutputPath, "en", "index.html"), Path.Combine(Options.OutputPath, "index.html"));
    }

    private async Task ProcessPage(string bookUrlName, string bookFolderName, (string title, string file, string url) page, string language, string destinationPath)
    {
        var sourcePath = Path.Combine(Options.SourcePath, language, bookFolderName, page.file);

        if (!File.Exists(sourcePath))
        {
            if (language == "en")
            {
                Console.Error.WriteLine($"Could not find {sourcePath}, skipped!");
                return;
            }

            sourcePath = Path.Combine(Options.SourcePath, "en", bookFolderName, page.file);

            if (!File.Exists(sourcePath))
            {
                return;
            }
        }

        var html = await RazorTemplateEngine.RenderAsync("/DocumentPage.cshtml", new DocumentPageModel
        {
            RootUrlPrefix = RootUrlPrefix,
            BookUrlName = bookUrlName,
            BookFolderName = bookFolderName,
            Page = page,
            Language = language,
            Contents = new HtmlString(File.ReadAllText(sourcePath)),
        });
        var parser = new HtmlParser();
        var document = await parser.ParseDocumentAsync(html);

        Transform(document, bookUrlName, bookFolderName, language, page);

        using var sw = new StreamWriter(destinationPath);
        document.ToHtml(sw, HtmlMarkupFormatter.Instance);
    }

    private void Transform(IHtmlDocument document, string bookUrlName, string bookFolderName, string language, (string title, string file, string url) page)
    {
        Console.WriteLine($"Transforming {language}/{page.url}..");

        document.Title = page.title;

        var dummyRoot = OperatingSystem.IsWindows() ? "C:/en/" : "/en/";
        var dummyFolder = Path.Combine(dummyRoot, bookFolderName, Path.GetDirectoryName(page.file) ?? "");

        foreach (var a in document.Descendants<IHtmlAnchorElement>())
        {
            if (a.GetAttribute("href") is string href && !String.IsNullOrWhiteSpace(href))
            {
                string? hash = null;
                var hashIndex = href.IndexOf('#');
                if (hashIndex > -1)
                {
                    hash = href[hashIndex..];
                    href = href[..hashIndex];
                }

                if (!String.IsNullOrWhiteSpace(href))
                {
                    href = href.Replace("{pageUrl}", page.url);

                    if (page.url.LastIndexOf('/') is int lastSlash and > -1)
                    {
                        href = href.Replace("{parentUrl}", page.url[..lastSlash]);
                    }
                    else
                    {
                        href = href.Replace("{parentUrl}", "");
                    }

                    if (Uri.IsWellFormedUriString(href, UriKind.Relative))
                    {
                        var fullPath = Path.GetFullPath(href, dummyFolder).Replace('\\', '/');

                        if (fullPath.StartsWith(dummyRoot) && PageInfo.TryGetValue(fullPath[dummyRoot.Length..], out var link))
                        {
                            a.SetAttribute("href", $"{RootUrlPrefix}/{language}/{link.url}{hash}");

                            if (String.IsNullOrWhiteSpace(a.Title) || a.Title.Contains(':'))
                            {
                                a.Title = link.title;
                            }
                        }
                        else if (fullPath.StartsWith(OperatingSystem.IsWindows() ? $"C:{RootUrlPrefix}/" : $"{RootUrlPrefix}/"))
                        {
                            var secondSlash = fullPath.IndexOf('/', 3);

                            a.SetAttribute("href", $"{RootUrlPrefix}{fullPath[secondSlash..]}{hash}");
                        }
                        else
                        {
                            Console.Error.WriteLine($"  Mapping unknown for href attribute value: {href}");
                        }
                    }
                    else if (!href.StartsWith('#')
                        && !href.StartsWith("mailto:")
                        && !href.StartsWith("javascript:")
                        && !Uri.IsWellFormedUriString(href, UriKind.Absolute))
                    {
                        Console.Error.WriteLine($"  Unrecognized href attribute value: {href}");
                    }
                }
            }
        }

        foreach (var img in document.Descendants<IHtmlImageElement>())
        {
            if (img.GetAttribute("src") is string src)
            {
                if (src.StartsWith("../images/"))
                {
                    img.SetAttribute("src", $"{RootUrlPrefix}/{language}/{bookUrlName}/{src["../".Length..]}");
                }
                else if (!Uri.IsWellFormedUriString(src, UriKind.Absolute))
                {
                    Console.Error.WriteLine($"  Unrecognized src attribute value: {src}");
                }
            }
        }
    }

    private void CopyImages(string outputRootFolder, string language, string bookFolderName, string bookUrlName)
    {
        var imagesFolder = Path.Combine(outputRootFolder, language, bookUrlName, "images");

        CopyDirectory(Path.Combine(Options.SourcePath, "en", bookFolderName, "images"), imagesFolder);

        if (language != "en")
        {
            CopyDirectory(Path.Combine(Options.SourcePath, language, bookFolderName, "images"), imagesFolder);
        }
    }

    private FrozenDictionary<string, (string url, string title)> GetPageInfo()
    {
        var mappings = new List<(string file, string url, string title)>();

        foreach (var bookFolder in Directory.EnumerateDirectories(Path.Combine(Options.SourcePath, "en")))
        {
            var bookFolderName = Path.GetFileName(bookFolder);

            foreach (var (title, file, url) in GetPagesInfo(GetPagesXml(Path.Combine(bookFolder, "book.xml"))))
            {
                mappings.Add(($"{bookFolderName}/{file}", url, title));
            }
        }

        return mappings.ToFrozenDictionary(m => m.file, m => (m.url, m.title), StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<XElement> GetPagesXml(string file)
        => XElement.Load(file).Descendants("page");

    private static IEnumerable<(string title, string file, string url)> GetPagesInfo(IEnumerable<XElement> pages)
        => from page in pages select (title: page.Attribute("title")!.Value, file: page.Attribute("file")!.Value, url: page.Attribute("url")!.Value);

    private static void CopyDirectory(string source, string destination, bool recursive = true, bool overwrite = true)
    {
        var src = new DirectoryInfo(source);

        if (!src.Exists)
        {
            throw new DirectoryNotFoundException($"Source directory not found: {src.FullName}");
        }

        Directory.CreateDirectory(destination);

        foreach (var file in src.EnumerateFiles())
        {
            file.CopyTo(Path.Combine(destination, file.Name), overwrite);
        }

        if (recursive)
        {
            foreach (var sub in src.EnumerateDirectories())
            {
                CopyDirectory(sub.FullName, Path.Combine(destination, sub.Name), recursive, overwrite);
            }
        }
    }
}
