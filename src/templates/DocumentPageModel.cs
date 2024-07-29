using Microsoft.AspNetCore.Html;

namespace OriginLab.DocumentGeneration.Templates;

public class DocumentPageModel : WebPageModel
{
    public required string BookUrlName { get; set; }
    
    public required string BookFolderName { get; set; }

    public (string title, string file, string url) Page { get; set; }

    public (string title, string file, string url)? Parent { get; set; }
}
