using CommandLine;

namespace OriginLab.DocumentGeneration;

public class Options
{
    [Option('i', HelpText = "Only generate index pages, for fast iterations.")]
    public bool IndexPagesOnly { get; set; }

    [Value(0, Required = true, HelpText = "The src folder path to the books.")]
    public required string SourcePath { get; set; }
}
