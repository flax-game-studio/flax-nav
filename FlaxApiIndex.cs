using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Text;
using System.Xml;
using System.Xml.Linq;
namespace FlaxMcp.NavDaemon;

internal sealed record FlaxApiMember(
    char Kind,
    string FullName,
    string RawName,
    string SimpleName,
    string DeclType,
    string Namespace,
    bool IsRuntime,
    string BaseType,
    string Summary,
    string Remarks,
    string Returns,
    string Parameters,
    string ConstantValue,
    string LowerHaystack,
    string LowerSimpleName,
    string LowerFullName
);

internal static class FlaxApiIndex
{
    private static readonly object _lock = new();
    private static volatile bool _built;
    private static volatile bool _xmlMissing;
    private static List<FlaxApiMember> _members = new();
    private static Dictionary<string, FlaxApiMember> _typeByName = new(StringComparer.OrdinalIgnoreCase);
    private static string? _xmlPath;
    private static long _buildMs;
    private static string? _resolvedFrom;

    /// <summary>Deprecation map: XML doc key (e.g. "P:FlaxEngine.StaticModel
    /// .DrawPasses") → ObsoleteAttribute message ("Use DrawPass instead").
    /// Populated from FlaxEngine.CSharp.dll metadata — XML doc files carry no
    /// deprecation info, but the ObsoleteAttribute does. Keyed at member-FullName
    /// granularity (overloads collapse, which is acceptable for a hint).</summary>
    private static Dictionary<string, string> _deprecations = new(StringComparer.Ordinal);

    public static void EnsureBuilt()
    {
        if (_built) return;
        lock (_lock)
        {
            if (_built) return;
            if (_xmlMissing)
                throw new FileNotFoundException(
                    "FlaxEngine.CSharp.xml not found (cached negative). " +
                    "Looked under FLAXMCP_FLAX_XML env var and the standard " +
                    "Program Files Flax 1.12 install.");
            string? xml = ResolveXmlPath();
            if (xml == null)
            {
                _xmlMissing = true;
                throw new FileNotFoundException(
                    "FlaxEngine.CSharp.xml not found. " +
                    "Looked under FLAXMCP_FLAX_XML env var and the standard " +
                    "Program Files Flax 1.12 install.");
            }
            Build(xml);
            _built = true;
        }
    }

    private static string? ResolveXmlPath()
    {
        string? envFile = Environment.GetEnvironmentVariable("FLAXMCP_FLAX_XML");
        if (!string.IsNullOrWhiteSpace(envFile) && File.Exists(envFile))
        {
            _resolvedFrom = "env:FLAXMCP_FLAX_XML";
            return envFile;
        }

        var roots = new List<string>();
        string? envInstall = Environment.GetEnvironmentVariable("FLAXMCP_FLAX_INSTALL");
        if (!string.IsNullOrWhiteSpace(envInstall)) roots.Add(envInstall);
        string? flax12 = Environment.GetEnvironmentVariable("FLAX_1_12");
        if (!string.IsNullOrWhiteSpace(flax12)) roots.Add(flax12);
        roots.Add(@"C:\Program Files (x86)\Flax\Flax_1.12");

        foreach (var root in roots)
        {
            foreach (var cfg in new[] { "Development", "Debug", "Release" })
            {
                string p = Path.Combine(root, "Binaries", "Editor", "Win64", cfg, "FlaxEngine.CSharp.xml");
                if (File.Exists(p))
                {
                    _resolvedFrom = root == envInstall ? $"env:FLAXMCP_FLAX_INSTALL/{cfg}" : $"flax-1.12-install/{cfg}";
                    return p;
                }
            }
        }
        return null;
    }

    private static void Build(string xmlPath)
    {
        var sw = Stopwatch.StartNew();
        var members = new List<FlaxApiMember>(capacity: 16384);
        var settings = new XmlReaderSettings { IgnoreWhitespace = false, DtdProcessing = DtdProcessing.Prohibit };
        using var reader = XmlReader.Create(xmlPath, settings);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.Name != "member") continue;
            string? nameAttr = reader.GetAttribute("name");
            if (string.IsNullOrEmpty(nameAttr) || nameAttr.Length < 2 || nameAttr[1] != ':') continue;
            XElement el;
            try { el = (XElement)XNode.ReadFrom(reader); }
            catch (Exception ex) { SwallowedCatch.Record("FlaxApiIndex.ReadMembers.readEl", ex); try { reader.Skip(); } catch (Exception ex2) { SwallowedCatch.Record("FlaxApiIndex.ReadMembers.skip", ex2); break; } continue; }

            char kind = nameAttr[0];
            string raw = nameAttr[2..];
            int paren = raw.IndexOf('(');
            string full = paren >= 0 ? raw[..paren] : raw;
            string simple = full;
            int lastDot = full.LastIndexOf('.');
            if (lastDot >= 0 && lastDot < full.Length - 1) simple = full[(lastDot + 1)..];
            if (simple == "#ctor") simple = "ctor";

            string declType = "";
            if (kind != 'T' && lastDot >= 0)
            {
                string owner = full[..lastDot];
                int ownerDot = owner.LastIndexOf('.');
                declType = ownerDot >= 0 ? owner[(ownerDot + 1)..] : owner;
            }

            string baseType = "";
            if (kind == 'T')
            {
                var baseEl = el.Element("Base") ?? el.Element("base");
                if (baseEl != null)
                {
                    var tn = baseEl.Element("TypeName") ?? baseEl.Element("typename");
                    if (tn != null) baseType = tn.Value.Trim();
                }
            }

            string summary = Flatten(el.Element("summary"));
            string remarks = Flatten(el.Element("remarks"));
            string returns = Flatten(el.Element("returns"));

            string constVal = "";
            var constEl = el.Element("constant");
            if (constEl != null) constVal = Flatten(constEl);
            if (string.IsNullOrEmpty(constVal)) { var valueEl = el.Element("value"); if (valueEl != null) constVal = Flatten(valueEl); }

            var pbits = new List<string>();
            foreach (var p in el.Elements("param"))
            {
                string pn = p.Attribute("name")?.Value ?? "";
                string pt = Flatten(p);
                if (pn.Length == 0 && pt.Length == 0) continue;
                pbits.Add(pn.Length > 0 ? $"{pn}: {pt}" : pt);
            }
            string parameters = string.Join("; ", pbits);

            int firstDot = full.IndexOf('.');
            string ns = firstDot >= 0 ? full[..firstDot] : full;
            bool isRuntime = full.StartsWith("FlaxEngine.", StringComparison.Ordinal) || full == "FlaxEngine";

            string haystack = (simple + " " + full + " " + summary + " " + ns + " " + declType).ToLowerInvariant();

            members.Add(new FlaxApiMember(
                Kind: kind, FullName: full, RawName: raw, SimpleName: simple,
                DeclType: declType, Namespace: ns, IsRuntime: isRuntime, BaseType: baseType,
                Summary: summary, Remarks: remarks, Returns: returns,
                Parameters: parameters, ConstantValue: constVal,
                LowerHaystack: haystack,
                LowerSimpleName: simple.ToLowerInvariant(),
                LowerFullName: full.ToLowerInvariant()));
        }

        // Populate BaseType from FlaxEngine.CSharp.dll metadata (XML has no Base
        // elements). Metadata-only read — the previous Assembly.LoadFrom path
        // failed with "Could not load Newtonsoft.Json 13.0.2.0 (PublicKeyToken=
        // null)" because the engine DLL references Flax's unsigned Newtonsoft
        // fork, and that one failure aborted BaseType population for ALL types.
        try
        {
            string? dllDir = Path.GetDirectoryName(xmlPath);
            string? dllPath = dllDir != null ? Path.Combine(dllDir, "FlaxEngine.CSharp.dll") : null;
            if (dllPath != null && File.Exists(dllPath))
            {
                var baseTypes = ReadBaseTypesFromMetadata(dllPath);
                for (int i = 0; i < members.Count; i++)
                {
                    var m = members[i];
                    if (m.Kind == 'T' && string.IsNullOrEmpty(m.BaseType)
                        && baseTypes.TryGetValue(m.FullName, out string? bt))
                    {
                        members[i] = m with { BaseType = bt };
                    }
                }
                _deprecations = ReadDeprecationsFromMetadata(dllPath);
            }
        }
        catch (Exception ex)
        {
            DaemonLog.Warn($"Could not read FlaxEngine.CSharp.dll metadata for inheritance: {ex.Message}");
        }

        sw.Stop();
        _xmlPath = xmlPath;
        _members = members;
        // Build type-lookup dictionary for O(1) inheritance resolution
        var tDict = new Dictionary<string, FlaxApiMember>(members.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var m in members)
            if (m.Kind == 'T' && !tDict.ContainsKey(m.FullName))
                tDict[m.FullName] = m;
        _typeByName = tDict;
        _buildMs = sw.ElapsedMilliseconds;
    }

    /// <summary>Read the base type of every type definition straight from the
    /// DLL's metadata tables (System.Reflection.Metadata). Never loads the
    /// assembly or resolves its dependencies, so it works regardless of what
    /// the engine DLL references. Names are built with '.' separators for
    /// nested types to match the XML doc-comment 'T:' key format.</summary>
    private static Dictionary<string, string> ReadBaseTypesFromMetadata(string dllPath)
    {
        static string FullNameOfDef(System.Reflection.Metadata.MetadataReader r, System.Reflection.Metadata.TypeDefinitionHandle h)
        {
            var td = r.GetTypeDefinition(h);
            string name = r.GetString(td.Name);
            var declaring = td.GetDeclaringType();
            if (!declaring.IsNil) return FullNameOfDef(r, declaring) + "." + name;
            string ns = r.GetString(td.Namespace);
            return ns.Length > 0 ? ns + "." + name : name;
        }
        static string FullNameOfRef(System.Reflection.Metadata.MetadataReader r, System.Reflection.Metadata.TypeReferenceHandle h)
        {
            var tr = r.GetTypeReference(h);
            string name = r.GetString(tr.Name);
            if (tr.ResolutionScope.Kind == System.Reflection.Metadata.HandleKind.TypeReference)
                return FullNameOfRef(r, (System.Reflection.Metadata.TypeReferenceHandle)tr.ResolutionScope) + "." + name;
            string ns = r.GetString(tr.Namespace);
            return ns.Length > 0 ? ns + "." + name : name;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        using var fs = File.OpenRead(dllPath);
        using var pe = new System.Reflection.PortableExecutable.PEReader(fs);
        var md = pe.GetMetadataReader();
        foreach (var handle in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(handle);
            if (td.BaseType.IsNil) continue;
            string? baseName = td.BaseType.Kind switch
            {
                System.Reflection.Metadata.HandleKind.TypeReference =>
                    FullNameOfRef(md, (System.Reflection.Metadata.TypeReferenceHandle)td.BaseType),
                System.Reflection.Metadata.HandleKind.TypeDefinition =>
                    FullNameOfDef(md, (System.Reflection.Metadata.TypeDefinitionHandle)td.BaseType),
                _ => null, // TypeSpecification = generic instantiation; not representable as an XML 'T:' key
            };
            if (baseName == null || baseName == "System.Object") continue;
            map[FullNameOfDef(md, handle)] = baseName;
        }
        return map;
    }

    /// <summary>Read every [Obsolete] attribute straight from the DLL's
    /// metadata tables (System.Reflection.Metadata — never loads the
    /// assembly). Keys are XML doc style: kind letter + ':' + full member
    /// name (methods collapse overloads to the bare name — a hint, not a
    /// gate). The attribute blob is a SerString message, optionally followed
    /// by the error flag byte; only the message is kept.</summary>
    private static Dictionary<string, string> ReadDeprecationsFromMetadata(string dllPath)
    {
        static string FullNameOfDef(System.Reflection.Metadata.MetadataReader r, System.Reflection.Metadata.TypeDefinitionHandle h)
        {
            var td = r.GetTypeDefinition(h);
            string name = r.GetString(td.Name);
            var declaring = td.GetDeclaringType();
            if (!declaring.IsNil) return FullNameOfDef(r, declaring) + "." + name;
            string ns = r.GetString(td.Namespace);
            return ns.Length > 0 ? ns + "." + name : name;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        using var fs = File.OpenRead(dllPath);
        using var pe = new System.Reflection.PortableExecutable.PEReader(fs);
        var md = pe.GetMetadataReader();

        void ReadMemberCustomAttributes(string key, System.Collections.Generic.IEnumerable<CustomAttributeHandle> attrs)
        {
            foreach (var handle in attrs)
            {
                var attr = md.GetCustomAttribute(handle);
                if (!IsObsoleteAttribute(md, attr)) continue;
                string? msg = ParseObsoleteMessage(md, attr);
                if (msg != null && !map.ContainsKey(key)) map[key] = msg;
            }
        }

        foreach (var handle in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(handle);
            string fullName = FullNameOfDef(md, handle);
            ReadMemberCustomAttributes("T:" + fullName, td.GetCustomAttributes());

            foreach (var mh in td.GetMethods())
            {
                var mdef = md.GetMethodDefinition(mh);
                string name = md.GetString(mdef.Name);
                ReadMemberCustomAttributes("M:" + fullName + "." + name, mdef.GetCustomAttributes());
            }
            foreach (var ph in td.GetProperties())
            {
                var pdef = md.GetPropertyDefinition(ph);
                string name = md.GetString(pdef.Name);
                ReadMemberCustomAttributes("P:" + fullName + "." + name, pdef.GetCustomAttributes());
            }
            foreach (var fh in td.GetFields())
            {
                var fdef = md.GetFieldDefinition(fh);
                string name = md.GetString(fdef.Name);
                ReadMemberCustomAttributes("F:" + fullName + "." + name, fdef.GetCustomAttributes());
            }
            foreach (var eh in td.GetEvents())
            {
                var edef = md.GetEventDefinition(eh);
                string name = md.GetString(edef.Name);
                ReadMemberCustomAttributes("E:" + fullName + "." + name, edef.GetCustomAttributes());
            }
        }
        return map;
    }

    private static bool IsObsoleteAttribute(System.Reflection.Metadata.MetadataReader md, System.Reflection.Metadata.CustomAttribute attr)
    {
        if (attr.Constructor.Kind != System.Reflection.Metadata.HandleKind.MemberReference) return false;
        var ctor = md.GetMemberReference((System.Reflection.Metadata.MemberReferenceHandle)attr.Constructor);
        if (ctor.Parent.Kind != System.Reflection.Metadata.HandleKind.TypeReference) return false;
        var tr = md.GetTypeReference((System.Reflection.Metadata.TypeReferenceHandle)ctor.Parent);
        return md.GetString(tr.Name) == "ObsoleteAttribute"
               && md.GetString(tr.Namespace) == "System";
    }

    private static string? ParseObsoleteMessage(System.Reflection.Metadata.MetadataReader md, System.Reflection.Metadata.CustomAttribute attr)
    {
        try
        {
            var blob = md.GetBlobReader(attr.Value);
            if (blob.Length < 2) return null;
            if (blob.ReadUInt16() != 1) return null; // prolog
            if (blob.Length == 0) return "";
            int len;
            try { len = blob.ReadCompressedInteger(); }
            catch (BadImageFormatException) { return ""; }
            if (len <= 0) return "";
            if (len > blob.RemainingBytes) return "";
            return blob.ReadUTF8(len);
        }
        catch (Exception ex)
        {
            SwallowedCatch.Record("FlaxApiIndex.ParseObsoleteMessage", ex);
            return null;
        }
    }

    /// <summary>Look up an XML-doc-style deprecation key for a member.
    /// Returns the Obsolete message (possibly empty) when deprecated.</summary>
    public static bool TryGetDeprecation(FlaxApiMember m, out string message)
    {
        string key = m.Kind + ":" + m.FullName;
        return _deprecations.TryGetValue(key, out message!);
    }

    private static string Flatten(XElement? el)
    {
        if (el == null) return "";
        var sb = new StringBuilder();
        FlattenInto(el, sb);
        var outp = new StringBuilder(sb.Length);
        bool lastWs = false;
        foreach (char c in sb.ToString())
        {
            bool ws = c == ' ' || c == '\t' || c == '\r' || c == '\n';
            if (ws) { if (!lastWs) outp.Append(' '); lastWs = true; }
            else { outp.Append(c); lastWs = false; }
        }
        return outp.ToString().Trim();
    }

    private static void FlattenInto(XElement el, StringBuilder sb)
    {
        foreach (var node in el.Nodes())
        {
            switch (node)
            {
                case XText t: sb.Append(t.Value); break;
                case XElement child:
                    switch (child.Name.LocalName)
                    {
                        case "see": case "seealso": sb.Append(ShortRef(child)); break;
                        case "paramref": case "typeparamref": sb.Append(child.Attribute("name")?.Value ?? ""); break;
                        case "para": sb.Append(' '); FlattenInto(child, sb); sb.Append(' '); break;
                        case "br": sb.Append(' '); break;
                        default: FlattenInto(child, sb); break;
                    }
                    break;
            }
        }
    }

    private static string ShortRef(XElement see)
    {
        string? langword = see.Attribute("langword")?.Value;
        if (!string.IsNullOrEmpty(langword)) return langword;
        string? cref = see.Attribute("cref")?.Value;
        if (!string.IsNullOrEmpty(cref))
        {
            string s = cref;
            if (s.Length > 2 && s[1] == ':') s = s[2..];
            int paren = s.IndexOf('(');
            if (paren >= 0) s = s[..paren];
            var segs = s.Split('.');
            return segs.Length >= 2 ? segs[^2] + "." + segs[^1] : s;
        }
        string? href = see.Attribute("href")?.Value;
        if (!string.IsNullOrEmpty(href))
        {
            string inner = see.Value?.Trim() ?? "";
            return inner.Length > 0 ? inner : href;
        }
        return see.Value?.Trim() ?? "";
    }

    public static IReadOnlyList<FlaxApiMember> Members => _members;
    public static int MemberCount => _members.Count;
    public static long BuildMillis => _buildMs;
    public static string? ResolvedFrom => _resolvedFrom;
    public static bool Built => _built;

    /// <summary>Look up a type member by full name (ignoring case). O(1).</summary>
    public static FlaxApiMember? FindType(string fullName) =>
        _typeByName.TryGetValue(fullName, out var m) ? m : null;
}

