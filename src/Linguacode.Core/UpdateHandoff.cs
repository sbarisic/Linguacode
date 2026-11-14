namespace Linguacode.Core;

public sealed record UpdateHandoff(string Root, string Token, InstallationProcess Owner, string Package, string Sha256,
    string DataDirectory, string[] RestartArguments);
