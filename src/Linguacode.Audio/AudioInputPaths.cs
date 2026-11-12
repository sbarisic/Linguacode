using Linguacode.Core;
namespace Linguacode.Audio;
internal static class AudioInputPaths
{
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase) { ".git", "bin", "obj", "node_modules", ".vs", "artifacts" };
    public static string ValidateRoot(string workspace)
    {
        var root = Path.GetFullPath(workspace);
        if (!Directory.Exists(root)) throw new UserFacingException("Workspace directory does not exist.");
        for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new UserFacingException("Workspace paths through links or junctions are unsupported.");
        return Path.TrimEndingDirectorySeparator(root);
    }
    public static string Resolve(string workspace, string relative, bool allowMissing = false, bool imageAccess = false)
    {
        var root = ValidateRoot(workspace);
        if (Path.IsPathRooted(relative) || relative.Contains(':')) throw new UserFacingException("Use a workspace-relative path.");
        var target = Path.GetFullPath(Path.Combine(root, relative));
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!target.Equals(root, StringComparison.OrdinalIgnoreCase) && !target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new UserFacingException("Path is outside the workspace.");
        var current = root;
        foreach (var part in Path.GetRelativePath(root, target).Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part.EndsWith(' ') || part.EndsWith('.') || part.Any(c => c is '<' or '>' or '"' or '|' or '?' or '*') || System.Text.RegularExpressions.Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) throw new UserFacingException("Invalid Windows path component.");
            if (Ignored.Contains(part) && !(imageAccess && (part.Equals("artifacts", StringComparison.OrdinalIgnoreCase) || part.Equals("bin", StringComparison.OrdinalIgnoreCase) || part.Equals("obj", StringComparison.OrdinalIgnoreCase)))) throw new UserFacingException("Path is excluded from workspace access.");
            current = Path.Combine(current, part);
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new UserFacingException("Links and junctions are unsupported."); }
            catch (FileNotFoundException) when (allowMissing) { }
            catch (DirectoryNotFoundException) when (allowMissing) { }
        }
        return target;
    }
}
