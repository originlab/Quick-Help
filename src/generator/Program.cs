using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<string, HashSet<string>> ProblematicPages = new();

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
        PageInfo = GetPagesInfo();
    }

    private async Task BuildOutput(CancellationToken cancellation)
    {
        if (Directory.Exists(Options.OutputPath))
        {
            Console.WriteLine("Cleaning up output folder..");
            Directory.Delete(Options.OutputPath, true);
        }

        Directory.CreateDirectory(Options.OutputPath);

        foreach (var language in Languages)
        {
            Console.WriteLine($"Processing {language} pages..");

            Directory.CreateDirectory(Path.Combine(Options.OutputPath, language));

            if (!Options.IndexPagesOnly)
            {
                foreach (var bookFolder in Directory.EnumerateDirectories(Path.Combine(Options.SourcePath, "en")))
                {
                    var pages = GetPagesXml(Path.Combine(bookFolder, "book.xml")).Select(e => (self: GetPageInfo(e), parent: GetPageInfo(e.Parent))).ToList();
                    var bookUrlName = pages[0].self.url;
                    var bookFolderName = Path.GetFileName(bookFolder);

                    var copyImagesTask = CopyImages(Options.OutputPath, language, bookFolderName, bookUrlName, cancellation);

                    if (Options.DisableParallelProcessing)
                    {
                        await copyImagesTask;
                    }

                    var processPageTask = Parallel.ForEachAsync(pages,
                        new ParallelOptions
                        {
                            MaxDegreeOfParallelism = Options.DisableParallelProcessing ? 1 : -1,
                            CancellationToken = cancellation,
                        },
                        async (page, cancellation) =>
                        {
                            var pageFolder = Path.Combine(Options.OutputPath, language, page.self.url);

                            Directory.CreateDirectory(pageFolder);

                            await ProcessPage(bookUrlName, bookFolderName, page.self, page.parent, language, Path.Combine(pageFolder, "index.html"), cancellation);
                        }
                    );

                    await Task.WhenAll(copyImagesTask, processPageTask);
                }
            }

            await ProcessPage("", "", ("Index", "index.html", ""), null, language, Path.Combine(Options.OutputPath, language, "index.html"), cancellation);
        }

        File.Copy(Path.Combine(Options.OutputPath, "en", "index.html"), Path.Combine(Options.OutputPath, "index.html"));

        Console.WriteLine("============================================");
        Console.WriteLine($"Problematic files: {ProblematicPages.Count}, avg {ProblematicPages.Average(kvp => kvp.Value.Count)}");

        var commonProblems = from file in ProblematicPages.Keys
                             from description in ProblematicPages[file]
                             group file by description;

        foreach (var problem in commonProblems)
        {
            Console.WriteLine("-------------------------");
            Console.WriteLine(problem.Key);
            Console.WriteLine("Sources:");

            foreach (var source in problem)
            {
                Console.WriteLine(source);
            }
        }
    }

    private async Task ProcessPage(string bookUrlName, string bookFolderName, (string title, string file, string url) page, (string title, string file, string url)? parent, string language, string destinationPath, CancellationToken cancellation)
    {
        var sourcePath = Path.Combine(Options.SourcePath, language, bookFolderName, page.file);

        if (!File.Exists(sourcePath))
        {
            if (language == "en")
            {
                ReportProblem(sourcePath, $"Could not find source file, skipped!");
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
            Parent = parent,
            Language = language,
            Contents = new HtmlString(File.ReadAllText(sourcePath)),
        });
        var parser = new HtmlParser();
        var document = await parser.ParseDocumentAsync(html, cancellation);

        Transform(document, bookUrlName, bookFolderName, language, page, sourcePath);

        try
        {
            using var sw = new StreamWriter(destinationPath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Options = FileOptions.Asynchronous,
            });

            document.ToHtml(sw, HtmlMarkupFormatter.Instance);
        }
        catch (IOException)
        {
            ReportProblem(sourcePath, $"Failed to write contents to {destinationPath}");
        }
    }

    private void Transform(IHtmlDocument document, string bookUrlName, string bookFolderName, string language, (string title, string file, string url) page, string sourcePath)
    {
        if (Options.Verbose)
        {
            Console.WriteLine($"Transforming {language}/{page.url}..");
        }

        var basePath = Path.GetDirectoryName(sourcePath)!;

        document.Title = page.title;

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
                    if (!href.StartsWith('/'))
                    {
                        if (Uri.IsWellFormedUriString(href, UriKind.Relative))
                        {
                            var fullPath = Path.GetFullPath(href, basePath).Replace('\\', '/');

                            if (fullPath.StartsWith(Options.SourcePath.Replace('\\', '/')) && PageInfo.TryGetValue(fullPath[(Options.SourcePath.Length + 1 + language.Length + 1)..], out var link))
                            {
                                a.SetAttribute("href", $"{RootUrlPrefix}/{language}/{link.url}{hash}");

                                if (String.IsNullOrWhiteSpace(a.Title) || a.Title.Contains(':'))
                                {
                                    a.Title = link.title;
                                }
                            }
                            else
                            {
                                ReportProblem(sourcePath, $"Mapping unknown for href: {href}");
                            }
                        }
                        else if (!href.StartsWith('#')
                            && !href.StartsWith("mailto:")
                            && !href.StartsWith("javascript:")
                            && !Uri.IsWellFormedUriString(href, UriKind.Absolute))
                        {
                            ReportProblem(sourcePath, $"Unrecognized href: {href}");
                        }
                    }
                    else if (!href.StartsWith("/static/"))
                    {
                        ReportProblem(sourcePath, $"Unrecognized href: {href}");
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
                    ReportProblem(sourcePath, $"Unrecognized src: {src}");
                }
            }
        }
    }

    private void ReportProblem(string file, string description)
    {
        ProblematicPages.GetOrAdd(file[(Options.SourcePath.Length + 1)..], f => []).Add(description);

        if (Options.Verbose)
        {
            Console.Error.WriteLine($"[Problem] {file}: {description}");
        }
    }

    private async Task CopyImages(string outputRootFolder, string language, string bookFolderName, string bookUrlName, CancellationToken cancellation)
    {
        var imagesFolder = Path.Combine(outputRootFolder, language, bookUrlName, "images");

        var imageSource = new DirectoryInfo(Path.Combine(Options.SourcePath, "en", bookFolderName, "images"));
        if (imageSource.Exists)
        {
            await CopyDirectory(imageSource, imagesFolder, cancellation);
        }

        if (language != "en")
        {
            imageSource = new DirectoryInfo(Path.Combine(Options.SourcePath, language, bookFolderName, "images"));
            if (imageSource.Exists)
            {
                await CopyDirectory(imageSource, imagesFolder, cancellation);
            }
        }
    }

    private FrozenDictionary<string, (string url, string title)> GetPagesInfo()
    {
        var mappings = new List<(string file, string url, string title)>();

        foreach (var bookFolder in Directory.EnumerateDirectories(Path.Combine(Options.SourcePath, "en")))
        {
            var bookFolderName = Path.GetFileName(bookFolder);

            foreach (var (title, file, url) in GetPagesXml(Path.Combine(bookFolder, "book.xml")).Select(GetPageInfo))
            {
                mappings.Add(($"{bookFolderName}/{file}", url, title));
            }
        }

        return mappings.ToFrozenDictionary(m => m.file, m => (m.url, m.title), StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<XElement> GetPagesXml(string file)
        => XElement.Load(file).Descendants("page");

    private static (string title, string file, string url) GetPageInfo(XElement? page)
        => page is null || page.Name != "page"
        ? ("Index", "index.html", "")
        : (page.Attribute("title")!.Value, page.Attribute("file")!.Value, page.Attribute("url")!.Value);

    private async Task CopyDirectory(DirectoryInfo source, string destination, CancellationToken cancellation, bool recursive = true, bool overwrite = true)
    {
        if (!source.Exists)
        {
            Console.Error.WriteLine($"CopyDirectory: Source directory ({source}) not exists.");
            return;
        }

        Directory.CreateDirectory(destination);

        var copyFilesTask = Task.Factory.StartNew(() => CopyFiles(source, destination, overwrite), cancellation);

        if (Options.DisableParallelProcessing)
        {
            await copyFilesTask;
        }

        if (recursive)
        {
            var copyFoldersTask = Parallel.ForEachAsync(source.EnumerateDirectories(),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Options.DisableParallelProcessing ? 1 : -1,
                    CancellationToken = cancellation,
                },
                async (sub, cancel) =>
                {
                    await CopyDirectory(sub, Path.Combine(destination, sub.Name), cancel, recursive, overwrite);
                }
            );

            await Task.WhenAll(copyFilesTask, copyFoldersTask);
        }
    }

    static void CopyFiles(DirectoryInfo src, string destination, bool overwrite)
    {
        foreach (var file in src.EnumerateFiles())
        {
            file.CopyTo(Path.Combine(destination, file.Name), overwrite);
        }
    }
}
