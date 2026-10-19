using System.Collections.Concurrent;
using Cmf.Cli.Plugin.CodeReview.Models;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cmf.Cli.Plugin.CodeReview;

/// <summary>
/// Per-scan cache of everything derived from a source file's text: raw lines, the parsed syntax
/// root, method ranges, and loop state. A scan touches every file from several independent passes
/// (DEE analysis, call graph, the main rule loop), which previously re-read and re-parsed each
/// file 7-9 times; with the cache each derivation happens at most once per file per scan.
/// Thread-safe: the scanner warms it and runs the per-file rule loop in parallel. Each scan
/// creates its own cache so results can never go stale across scans.
/// </summary>
public sealed class CodeReviewFileCache
{
    private readonly ConcurrentDictionary<string, CachedSourceFile> _files = new(StringComparer.OrdinalIgnoreCase);

    public CachedSourceFile Get(string path) => _files.GetOrAdd(path, p => new CachedSourceFile(p));
}

/// <summary>One source file's lazily-derived analysis artifacts; each is computed on first access (thread-safe) and reused after.</summary>
public sealed class CachedSourceFile
{
    private readonly Lazy<string[]> _lines;
    private readonly Lazy<CompilationUnitSyntax> _root;
    private readonly Lazy<List<MethodRange>> _methodRanges;
    private readonly Lazy<LoopState> _loopState;

    public CachedSourceFile(string path)
    {
        Path = path;
        _lines = new Lazy<string[]>(ReadLines, LazyThreadSafetyMode.ExecutionAndPublication);
        _root = new Lazy<CompilationUnitSyntax>(() => CodeReviewSyntaxTree.Parse(Lines).Root, LazyThreadSafetyMode.ExecutionAndPublication);
        _methodRanges = new Lazy<List<MethodRange>>(() => CmMethodAnalyzer.GetMethodRanges(Root), LazyThreadSafetyMode.ExecutionAndPublication);
        _loopState = new Lazy<LoopState>(() => CmLoopAnalyzer.GetLoopState(Root), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string Path { get; }

    /// <summary>True when the file could not be read; the file then contributes nothing to any pass instead of killing the whole scan.</summary>
    public bool ReadFailed { get; private set; }

    public string[] Lines => _lines.Value;

    public CompilationUnitSyntax Root => _root.Value;

    /// <summary>Loop state including single-execution tails -- callers that only need loop membership just read <see cref="LoopState.Lines"/>.</summary>
    public LoopState LoopState => _loopState.Value;

    public List<MethodRange> MethodRanges => _methodRanges.Value;

    private string[] ReadLines()
    {
        try
        {
            return File.ReadAllLines(Path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // One unreadable file (locked by another process, permission-denied, deleted
            // mid-scan) must not abort a whole-repo scan. Warn on stderr -- stdout is the
            // machine-readable findings contract and must stay pure JSON.
            Console.Error.WriteLine($"warning: skipping unreadable file: {Path} ({e.Message})");
            ReadFailed = true;
            return Array.Empty<string>();
        }
    }
}
