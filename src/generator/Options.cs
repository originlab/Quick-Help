﻿using CommandLine;

namespace OriginLab.DocumentGeneration;

public class Options
{
    [Option('i', HelpText = "Only generate index pages, for fast iterations.")]
    public bool IndexPagesOnly { get; set; }

    [Option('s', HelpText = "Disable parallel processing, for debugging.")]
    public bool DisableParallelProcessing { get; set; }

    [Option('v', HelpText = "Print more details during generation.")]
    public bool Verbose { get; set; }

    private string? _SourcePath;

    [Value(0, Required = true, HelpText = "The folder path to the books.")]
    public required string SourcePath { get => _SourcePath!; set => _SourcePath = value.Replace('\\', '/'); }

    private string? _OutputPath;

    [Value(1, Required = true, HelpText = "The folder path to the outpot.")]
    public required string OutputPath { get => _OutputPath!; set => _OutputPath = value.Replace('\\', '/'); }
}
