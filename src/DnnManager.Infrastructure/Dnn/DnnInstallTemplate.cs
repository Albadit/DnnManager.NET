using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using DnnManager.Application.Abstractions;

namespace DnnManager.Infrastructure.Dnn;

/// <summary>
/// DNN's install template, <c>Install\DotNetNuke.install.config</c>: what DNN's unattended install creates - the host
/// account, the website and its alias. Written from the template DNN ships (<c>DotNetNuke.install.config.resources</c>),
/// so everything else stays as DNN has it.
/// </summary>
public static class DnnInstallTemplate
{
    public const string FileName = "DotNetNuke.install.config";
    public const string ShippedFileName = FileName + ".resources";

    /// <summary>
    /// Writes the template into <paramref name="siteDirectory"/>'s Install folder and returns its path. Values go in through
    /// XML, so characters like &amp; and &lt; can't break it. Leaves out DNN's &lt;connection&gt; node on purpose: with it
    /// DNN rewrites web.config's connection string itself (and forces "User Instance" for a database file).
    /// </summary>
    public static string Write(string siteDirectory, DnnAccount account, string portalAlias)
    {
        var installDirectory = Path.Combine(siteDirectory, "Install");
        var shipped = Path.Combine(installDirectory, ShippedFileName);
        if (!File.Exists(shipped)) throw new FileNotFoundException("DNN's install template isn't in the package.", shipped);

        var doc = XDocument.Load(shipped, LoadOptions.PreserveWhitespace);
        var root = doc.Root ?? throw new InvalidDataException("DNN's install template is empty.");
        if (root.Name.LocalName != "dotnetnuke") throw new InvalidDataException($"DNN's install template starts with <{root.Name}>, not <dotnetnuke>.");

        // Read as "is it en-US?": a missing node would have DNN remove en-US from the site.
        SetChild(root, "installCulture", account.Language);

        var superuser = GetOrAdd(root, "superuser");
        SetChild(superuser, "username", account.UserName);
        SetChild(superuser, "password", account.Password);
        SetChild(superuser, "email", account.Email);
        SetChild(superuser, "locale", account.Language);
        SetChild(superuser, "updatepassword", "false");

        SetChild(GetOrAdd(root, "settings"), "HostEmail", account.Email);
        root.Elements("connection").Remove();

        var portals = GetOrAdd(root, "portals");
        var portal = portals.Elements("portal").FirstOrDefault() ?? Add(portals, "portal");
        foreach (var extra in portals.Elements("portal").Skip(1).ToList()) extra.Remove();
        SetChild(portal, "portalname", account.WebsiteName);
        // DNN needs the node to create the site; since DNN 9.3 the host becomes the site's administrator and these
        // credentials aren't used - a random password keeps the shipped default out of it.
        var administrator = portal.Element("administrator") ?? Add(portal, "administrator");
        if (administrator.Element("username") is null) SetChild(administrator, "username", "admin");
        SetChild(administrator, "password", RandomPassword());
        SetChild(portal, "templatefile", account.Template + ".template");
        var aliases = GetOrAdd(portal, "portalaliases");
        aliases.RemoveNodes();
        aliases.Add(new XElement("portalalias", portalAlias));
        SetChild(portal, "ischild", "false");

        var target = Path.Combine(installDirectory, FileName);
        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), OmitXmlDeclaration = doc.Declaration is null };
        using (var writer = XmlWriter.Create(target, settings)) doc.Save(writer);
        return target;
    }

    /// <summary>Deletes the written template and DNN's shipped one - both are read by an installer that asks no password.</summary>
    public static void Delete(string siteDirectory)
    {
        foreach (var name in new[] { FileName, ShippedFileName })
        {
            var path = Path.Combine(siteDirectory, "Install", name);
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string RandomPassword() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));

    private static XElement GetOrAdd(XElement parent, string name) => parent.Element(name) ?? Add(parent, name);

    private static XElement Add(XElement parent, string name)
    {
        var element = new XElement(name);
        parent.Add(element);
        return element;
    }

    private static void SetChild(XElement parent, string name, string value)
    {
        var element = parent.Element(name);
        if (element is null) parent.Add(new XElement(name, value));
        else element.Value = value;
    }
}
