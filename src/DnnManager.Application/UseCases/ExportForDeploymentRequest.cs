namespace DnnManager.Application.UseCases;

/// <summary>What Export for deployment makes, as its dialog asks for it.</summary>
public sealed class ExportForDeploymentRequest
{
    public required string ProjectName { get; init; }

    /// <summary>The folder the site serves (its IIS physical path).</summary>
    public required string ProjectDirectory { get; init; }

    /// <summary>The folder the package is written to: &lt;project&gt;.zip, &lt;project&gt;.bacpac and DEPLOY.txt.</summary>
    public required string OutputFolder { get; init; }

    /// <summary>
    /// The live site's domains, e.g. <c>www.example.com</c> - the first is the primary portal alias. Empty: the database's
    /// aliases are left as they are.
    /// </summary>
    public IReadOnlyList<string> Domains { get; init; } = [];

    /// <summary>The live site uses HTTPS: DNN's SSL setting is switched on and the HTTPS rules DNN Manager switched off are back on.</summary>
    public bool Https { get; init; }

    /// <summary>The live database's connection string for web.config; null or empty writes a placeholder to fill in on the server.</summary>
    public string? ConnectionString { get; init; }

    /// <summary>The database as a .bacpac too; false: only the site's files.</summary>
    public bool IncludeDatabase { get; init; } = true;
}
