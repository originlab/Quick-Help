using Microsoft.AspNetCore.Html;

namespace OriginLab.DocumentGeneration.Templates;

public class WebPageModel
{
    public required string Language { get; set; }

    public required IHtmlContent Contents { get; set; }
}
