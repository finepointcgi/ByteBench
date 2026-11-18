using LibGit2Sharp;
using LgRepository = LibGit2Sharp.Repository;

namespace GitBench.Services.Ci;

/// <summary>Exports a commit's tree into a plain directory that a job's containers mount at <c>/workspace</c>.</summary>
public static class CiWorkspace
{
    /// <summary>
    /// Writes every file of <paramref name="sha"/> under <paramref name="target"/> (which must not exist yet).
    /// Executable files keep their executable bit. Submodules are skipped. Symlinks are created only after every
    /// regular file is written, so no file is ever written through one, and links whose path would leave the
    /// workspace are skipped.
    /// </summary>
    public static void Export(string repoPath, string sha, string target)
    {
        Directory.CreateDirectory(target);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target)) + Path.DirectorySeparatorChar;
        var links = new List<(string Path, string Target)>();

        using var repo = new LgRepository(repoPath);
        var commit = repo.Lookup<Commit>(sha) ?? throw new InvalidOperationException($"Commit {sha} was not found.");
        Write(commit.Tree, root);

        foreach (var (path, linkTarget) in links)
        {
            try
            {
                if (!File.Exists(path) && !Directory.Exists(path)) File.CreateSymbolicLink(path, linkTarget);
            }
            catch (IOException) { /* name collision on a case-insensitive file system: skip */ }
        }
        return;

        void Write(Tree tree, string dir)
        {
            foreach (var entry in tree)
            {
                if (entry.Name is "." or ".." or ".git" || entry.Name.Contains('/') || entry.Name.Contains('\\')) continue;
                var path = Path.GetFullPath(Path.Combine(dir, entry.Name));
                if (!path.StartsWith(root, StringComparison.Ordinal)) continue;

                switch (entry.TargetType)
                {
                    case TreeEntryTargetType.Tree:
                        Directory.CreateDirectory(path);
                        Write((Tree)entry.Target, path + Path.DirectorySeparatorChar);
                        break;
                    case TreeEntryTargetType.Blob when entry.Mode == Mode.SymbolicLink:
                        links.Add((path, ((Blob)entry.Target).GetContentText()));
                        break;
                    case TreeEntryTargetType.Blob:
                        using (var src = ((Blob)entry.Target).GetContentStream())
                        using (var dst = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                            src.CopyTo(dst);
                        if (entry.Mode == Mode.ExecutableFile && !OperatingSystem.IsWindows())
                            File.SetUnixFileMode(path, File.GetUnixFileMode(path)
                                | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
                        break;
                    // GitLink (submodule): not fetched.
                }
            }
        }
    }
}
