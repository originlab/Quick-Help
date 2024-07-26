using Microsoft.AspNetCore.Html;

namespace OriginLab.DocumentGeneration.Templates;

public class DocumentPageModel
{
    public required string BookUrlName { get; set; }
    
    public required string BookFolderName { get; set; }

    public required string Language { get; set; }
    
    public (string title, string file, string url) Page { get; set; }

    public required HtmlString MainContents { get; set; }
}
