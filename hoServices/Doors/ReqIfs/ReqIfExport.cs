using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using hoLinqToSql.DataModels;
using hoUtils;
using hoUtils.Compression;
using hoUtils.DirFile;
using JetBrains.Annotations;
using ReqIFSharp;

namespace EaServices.Doors.ReqIfs
{
    public  class ReqIfExport:ReqIf
    {
        // Module specific, has to be estimated from ReqIF for a new module
        SpecificationType _specificationTypeModule;
        SpecObjectType _specObjectType;

        private bool _errorTvReported;

        // Module specification (hierarchy)
        Specification _moduleSpecification;

        // Handler embedded files
        private ExportEmbeddedEaFiles _exportEmbeddedEaFile;

        // ExportMappingList: ReqIF attributes filled from EA element properties (e.g. "MyStatus=EA.Status")
        private ExportFields _exportMapping;

        // Macro to export the text of all Notes linked to an element (EA NoteLink)
        private const string MacroLinkedNotes = "EA.LinkedNotes";
        // Macros to export the GUIDs of related elements (traceability)
        // - EA.TraceTo:   Elements the current element traces to   (current element = source of «trace»)
        // - EA.TraceFrom: Elements which trace to the current element (current element = target of «trace»)
        // - EA.LinksTo:   Like EA.TraceTo,   but all connector types (except NoteLink)
        // - EA.LinksFrom: Like EA.TraceFrom, but all connector types (except NoteLink)
        private const string MacroTraceTo = "EA.TraceTo";
        private const string MacroTraceFrom = "EA.TraceFrom";
        private const string MacroLinksTo = "EA.LinksTo";
        private const string MacroLinksFrom = "EA.LinksFrom";
        // Macros to export the elements linked by a «trace» relationship (both directions)
        private const string MacroTraceIds = "EA.TraceIds";       // EA Element ID, e.g. "123, 456"
        private const string MacroTraceGuids = "EA.TraceGuids";   // EA GUID
        // Macros to export the elements the current element is nested in (EA ParentID)
        // - EA.ParentGuid:  GUID of the direct parent element (empty if the element is directly in the package)
        // - EA.ParentName:  Name of the direct parent element
        // - EA.ParentGuids: GUIDs of all parent elements, outermost first
        // - EA.ParentPath:  Names of all parent elements, outermost first, e.g. "System / Subsystem / Function"
        private const string MacroParentGuid = "EA.ParentGuid";
        private const string MacroParentName = "EA.ParentName";
        private const string MacroParentGuids = "EA.ParentGuids";
        private const string MacroParentPath = "EA.ParentPath";
        // PackageIDs to export: the package, and with 'IncludeSubPackages' all sub-packages (recursive)
        private System.Collections.Generic.List<long> _packageIds = new System.Collections.Generic.List<long>();
        // true: Note elements are exported as attribute of the linked element, not as own requirement
        private bool _skipNoteElements;

        /// <inheritdoc />
        public ReqIfExport(EA.Repository rep, EA.Package pkg, string importFile, FileImportSettingsItem settings) :
            base(rep, pkg, importFile, settings)
        {

        }

        /// <summary>
        /// Export Requirements according to TaggedValues/AttributeNames in _settings.WriteAttrNameList
        /// </summary>
        /// <param name="subModuleIndex"></param>
        /// <returns></returns>
        public bool ExportRequirements(int subModuleIndex = 0)
        {
            _subModuleIndex = subModuleIndex;
            // Initialize
            if (_subModuleIndex == 0)
            {
                // delete files and create directories for files to deliver:
                // - Embedded files from EA 
                // - Embedded png files for images in rtf
                // - MimeTypeImages (files from installation)
                // 
                DirectoryExtension.CreateEmptyDir(Settings.EmbeddedFileStorageDictionary);
                DirectoryExtension.CreateEmptyDir(Path.Combine(Settings.EmbeddedFileStorageDictionary, Settings.EmbeddedFiles));
                DirectoryExtension.CreateEmptyDir(Path.Combine(Settings.EmbeddedFileStorageDictionary, Settings.EmbeddedFilesPng));

                // copy mimetype specific images
                string installationPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string mimeTypeImagesInstallationPath = Path.Combine(installationPath, Settings.MimeTypeImages);
                string mimeTypeTypeImagesInReqIf = Path.Combine(Settings.EmbeddedFileStorageDictionary, Settings.MimeTypeImages);
                DirectoryExtension.CreateEmptyDir(mimeTypeTypeImagesInReqIf);
                DirectoryExtension.DirectoryCopy(mimeTypeImagesInstallationPath, mimeTypeTypeImagesInReqIf);


            }

            _exportEmbeddedEaFile = new ExportEmbeddedEaFiles(
                Settings.EmbeddedFileStorageDictionary,
                Settings.MimeTypeImages, 
                Settings.EmbeddedFiles);
            // Calculate the column/taggedValueType prefix for current module
            _prefixTv = "";
            _errorMessage1 = false;
            _exportFields = new ExportFields(Settings.WriteAttrNameList);
            _exportMapping = new ExportFields(GetExportMappings());
            _skipNoteElements = _exportMapping.GetFields().Any(x => _exportMapping.GetMacroName(x) == MacroLinkedNotes);
            _packageIds = new System.Collections.Generic.List<long>();
            CollectPackageIds(Pkg, _packageIds, Settings.IncludeSubPackages);

            // Write header, delete file if first package
            ReqIfDeserialized = AddHeader(ImportModuleFile, newFile:subModuleIndex==0);
            if (ReqIfDeserialized == null) return false;
            _reqIfContent = ReqIfDeserialized.CoreContent.SingleOrDefault();
            _specificationTypeModule = (SpecificationType)_reqIfContent.SpecTypes.SingleOrDefault(x => x.GetType() == typeof(SpecificationType));

            // Write the requirements for the current module/specification
            WriteModuleSpecification(Pkg);

            if (! CreateSpecObjects()) return false;
            if (!CreateSpecHierarchy()) return false;

            // serialize ReqIF
            string fileReqIf = Path.Combine(Zip.CreateTempDir(), Path.GetFileName(ImportModuleFile));
            SerializeReqIf(fileReqIf, compress:false);

            if (!Compress(ImportModuleFile, Path.GetDirectoryName(fileReqIf), Settings.EmbeddedFileStorageDictionary)) return false;

            // Optional csv export. The ReqIF content contains all modules exported so far,
            // so the csv is re-written for each module and is complete after the last one.
            if (!String.IsNullOrWhiteSpace(Settings.CsvExportFile)) return WriteCsv(Settings.CsvExportFile);
            return true;


        }
        /// <summary>
        /// Get the valid entries of 'ExportMappingList' ("AttributeName=EA.Property").
        /// Entries whose attribute name starts with 'ReqIF.' are ignored, these ReqIF standard
        /// attributes are written by hoReverse itself.
        /// </summary>
        /// <returns></returns>
        private System.Collections.Generic.List<string> GetExportMappings()
        {
            return Settings.ExportMappingList
                .Select(x => x.Trim().TrimEnd(';'))
                .Where(x =>
                {
                    var parts = x.Split('=');
                    return parts.Length == 2 &&
                           !String.IsNullOrWhiteSpace(parts[0]) &&
                           !parts[0].Trim().StartsWith("ReqIF.") &&
                           parts[1].Trim().StartsWith("EA.");
                })
                .Select(x =>
                {
                    var parts = x.Split('=');
                    return $"{parts[0].Trim()}={parts[1].Trim()}";
                })
                .ToList();
        }

        /// <summary>
        /// Get the text of all Notes linked to the element by an EA NoteLink, as EA notes format.
        /// Several Notes are separated by an empty line.
        /// </summary>
        /// <param name="el"></param>
        /// <returns></returns>
        private string GetLinkedNotes(EA.Element el)
        {
            var notes = new System.Collections.Generic.List<string>();
            var seen = new System.Collections.Generic.HashSet<int>();
            foreach (EA.Connector con in el.Connectors)
            {
                if (con.Type != "NoteLink") continue;
                int otherId = con.ClientID == el.ElementID ? con.SupplierID : con.ClientID;
                if (otherId == el.ElementID || !seen.Add(otherId)) continue;
                EA.Element other;
                try
                {
                    other = Rep.GetElementByID(otherId);
                }
                catch
                {
                    continue;
                }
                if (other == null || String.IsNullOrWhiteSpace(other.Notes)) continue;
                notes.Add(other.Notes.Trim());
            }
            return String.Join("\r\n\r\n", notes);
        }

        /// <summary>
        /// Get the elements linked to 'el' by a «trace» relationship (incoming and outgoing).
        /// EA models a trace as Abstraction with stereotype 'trace'.
        /// Returns the IDs or GUIDs separated by ', ', sorted by Element ID.
        /// </summary>
        /// <param name="el"></param>
        /// <param name="useGuid"></param>
        /// <returns></returns>
        private string GetTraceElements(EA.Element el, bool useGuid)
        {
            var ids = new System.Collections.Generic.SortedSet<int>();
            foreach (EA.Connector con in el.Connectors)
            {
                if (!IsTrace(con)) continue;
                int otherId = con.ClientID == el.ElementID ? con.SupplierID : con.ClientID;
                if (otherId != el.ElementID && otherId > 0) ids.Add(otherId);
            }
            if (!useGuid) return String.Join(", ", ids);

            var guids = new System.Collections.Generic.List<string>();
            foreach (int id in ids)
            {
                try
                {
                    var other = Rep.GetElementByID(id);
                    if (other != null) guids.Add(GuidWithoutBraces(other.ElementGUID));
                }
                catch
                {
                    // element not available, skip
                }
            }
            return String.Join(", ", guids);
        }

        /// <summary>
        /// Check if the connector is a «trace» (EA Abstraction/Dependency with stereotype 'trace', SysML Trace,..)
        /// </summary>
        /// <param name="con"></param>
        /// <returns></returns>
        private static bool IsTrace(EA.Connector con)
        {
            if (String.Equals(con.Type, "Trace", StringComparison.OrdinalIgnoreCase)) return true;
            if (String.Equals(con.Stereotype, "trace", StringComparison.OrdinalIgnoreCase)) return true;
            return (con.StereotypeEx ?? "")
                .Split(',')
                .Select(x => x.Contains("::") ? x.Substring(x.LastIndexOf("::", StringComparison.Ordinal) + 2) : x)
                .Any(x => String.Equals(x.Trim(), "trace", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Get the GUIDs of related elements, one GUID per line, sorted.
        /// </summary>
        /// <param name="el">Current element</param>
        /// <param name="outgoing">true: current element is source (client), false: current element is target (supplier)</param>
        /// <param name="onlyTrace">true: only «trace» connectors, false: all connectors except NoteLink</param>
        /// <returns></returns>
        private string GetRelatedGuids(EA.Element el, bool outgoing, bool onlyTrace)
        {
            var guids = new System.Collections.Generic.SortedSet<string>();
            foreach (EA.Connector con in el.Connectors)
            {
                if (con.Type == "NoteLink") continue;
                if (onlyTrace && !IsTrace(con)) continue;
                int sourceId = con.ClientID;
                int targetId = con.SupplierID;
                if (sourceId == targetId) continue;
                if (outgoing && sourceId != el.ElementID) continue;
                if (!outgoing && targetId != el.ElementID) continue;
                int otherId = outgoing ? targetId : sourceId;
                try
                {
                    var other = Rep.GetElementByID(otherId);
                    if (other != null) guids.Add(GuidWithoutBraces(other.ElementGUID));
                }
                catch
                {
                    // ignore connectors to not existing elements
                }
            }
            return String.Join(", ", guids);
        }

        /// <summary>
        /// Get all elements the element is nested in (EA ParentID), outermost first.
        /// Packages are not included.
        /// </summary>
        /// <param name="el"></param>
        /// <returns></returns>
        private System.Collections.Generic.List<EA.Element> GetParentElements(EA.Element el)
        {
            var parents = new System.Collections.Generic.List<EA.Element>();
            var seen = new System.Collections.Generic.HashSet<int> { el.ElementID };
            int parentId = el.ParentID;
            while (parentId > 0 && seen.Add(parentId))
            {
                EA.Element parent;
                try
                {
                    parent = Rep.GetElementByID(parentId);
                }
                catch
                {
                    break;
                }
                if (parent == null) break;
                parents.Insert(0, parent);
                parentId = parent.ParentID;
            }
            return parents;
        }

        /// <summary>
        /// EA GUID without the curly braces: "{4A0F...}" => "4A0F..."
        /// </summary>
        /// <param name="guid"></param>
        /// <returns></returns>
        private static string GuidWithoutBraces(string guid)
        {
            return (guid ?? "").Trim().TrimStart('{').TrimEnd('}');
        }

        /// <summary>
        /// Make HTML of all scenarios of the element (Use Case scenarios), e.g.:
        /// Basic Path: Order product
        ///   notes
        ///   1. [Context] User selects product
        ///   2. [System] System shows price (Alternate 2a: Product not available)
        /// Returns "" if the element has no scenarios.
        /// </summary>
        /// <param name="el"></param>
        /// <returns></returns>
        private string MakeScenarioHtml(EA.Element el)
        {
            try
            {
                if (el.Scenarios == null || el.Scenarios.Count == 0) return "";
                var sb = new System.Text.StringBuilder();
                sb.Append("<p><b>Scenarios</b></p>");
                foreach (EA.Scenario scenario in el.Scenarios)
                {
                    string title = String.IsNullOrWhiteSpace(scenario.Type)
                        ? scenario.Name
                        : $"{scenario.Type}: {scenario.Name}";
                    sb.Append($"<p><b>{HtmlEncode(title)}</b></p>");
                    if (!String.IsNullOrWhiteSpace(scenario.Notes))
                        sb.Append(Rep.GetFormatFromField("HTML", scenario.Notes));

                    var steps = new System.Collections.Generic.List<EA.ScenarioStep>();
                    foreach (EA.ScenarioStep step in scenario.Steps) steps.Add(step);
                    if (steps.Count == 0) continue;

                    sb.Append("<ol>");
                    foreach (var step in steps.OrderBy(x => x.Pos))
                    {
                        string who = step.StepType.ToString().IndexOf("Actor", StringComparison.OrdinalIgnoreCase) >= 0
                            ? "Context"
                            : "System";
                        var text = new System.Text.StringBuilder($"[{who}] {HtmlEncode(step.Name)}");
                        if (!String.IsNullOrWhiteSpace(step.Uses)) text.Append($"<br/>Uses: {HtmlEncode(step.Uses)}");
                        if (!String.IsNullOrWhiteSpace(step.Results)) text.Append($"<br/>Results: {HtmlEncode(step.Results)}");
                        if (!String.IsNullOrWhiteSpace(step.State)) text.Append($"<br/>State: {HtmlEncode(step.State)}");
                        foreach (EA.ScenarioExtension ext in step.Extensions)
                        {
                            text.Append($"<br/>Extension {HtmlEncode(ext.Level)}: {HtmlEncode(ext.Name)}");
                        }
                        sb.Append($"<li>{text}</li>");
                    }
                    sb.Append("</ol>");
                }
                return sb.ToString();
            }
            catch (Exception e)
            {
                MessageBox.Show($@"Element: '{el.Name}'

{e}", @"Error exporting scenarios, scenarios skipped");
                return "";
            }
        }

        private static string HtmlEncode(string text)
        {
            return System.Net.WebUtility.HtmlEncode((text ?? "").Trim()).Replace("\r\n", "<br/>").Replace("\n", "<br/>");
        }

        /// <summary>
        /// Get the ReqIF XHTML value of an ExportMappingList attribute
        /// </summary>
        /// <param name="el"></param>
        /// <param name="mappedName"></param>
        /// <returns></returns>
        private string GetMappedXhtmlValue(EA.Element el, string mappedName)
        {
            switch (_exportMapping.GetMacroName(mappedName))
            {
                case MacroLinkedNotes:
                    string notes = GetLinkedNotes(el);
                    return String.IsNullOrEmpty(notes)
                        ? MakeXhtmlFromString("")
                        : MakeXhtmlFromEaNotes(Rep, notes);
                case MacroTraceIds:
                    return MakeXhtmlFromString(GetTraceElements(el, useGuid: false));
                case MacroTraceGuids:
                    return MakeXhtmlFromString(GetTraceElements(el, useGuid: true));
                case MacroTraceTo:
                    return MakeXhtmlFromString(GetRelatedGuids(el, outgoing: true, onlyTrace: true));
                case MacroTraceFrom:
                    return MakeXhtmlFromString(GetRelatedGuids(el, outgoing: false, onlyTrace: true));
                case MacroLinksTo:
                    return MakeXhtmlFromString(GetRelatedGuids(el, outgoing: true, onlyTrace: false));
                case MacroLinksFrom:
                    return MakeXhtmlFromString(GetRelatedGuids(el, outgoing: false, onlyTrace: false));
                case "EA.GUID":
                    return MakeXhtmlFromString(GuidWithoutBraces(el.ElementGUID));
                case MacroParentGuid:
                    return MakeXhtmlFromString(GetParentElements(el).Select(x => GuidWithoutBraces(x.ElementGUID)).LastOrDefault() ?? "");
                case MacroParentName:
                    return MakeXhtmlFromString(GetParentElements(el).Select(x => x.Name).LastOrDefault() ?? "");
                case MacroParentGuids:
                    return MakeXhtmlFromString(String.Join(", ", GetParentElements(el).Select(x => GuidWithoutBraces(x.ElementGUID))));
                case MacroParentPath:
                    return MakeXhtmlFromString(String.Join(" / ", GetParentElements(el).Select(x => x.Name)));
            }
            return MakeXhtmlFromString(_exportMapping.GetMacroValue(el, mappedName));
        }

        /// <summary>
        /// Write all exported requirements as csv:
        /// - One row per requirement in hierarchy order
        /// - Columns: Specification, Level, then all ReqIF attributes (union over all modules)
        /// - UTF-8 with BOM, all values quoted, XHTML converted to plain text
        /// </summary>
        /// <param name="csvFile"></param>
        /// <returns></returns>
        private bool WriteCsv(string csvFile)
        {
            try
            {
                string del = Settings.CsvDelimiter;
                var columns = new System.Collections.Generic.List<string>();
                foreach (var specType in _reqIfContent.SpecTypes.OfType<SpecObjectType>())
                {
                    foreach (var attr in specType.SpecAttributes)
                    {
                        if (!columns.Contains(attr.LongName)) columns.Add(attr.LongName);
                    }
                }

                var sb = new System.Text.StringBuilder();
                var header = new[] {"Specification", "Level"}.Concat(columns);
                sb.Append(String.Join(del, header.Select(CsvQuote))).Append("\r\n");

                foreach (var spec in _reqIfContent.Specifications)
                {
                    AddCsvRows(sb, del, columns, spec.LongName, spec.Children, 1);
                }

                string dir = Path.GetDirectoryName(csvFile);
                if (!String.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(csvFile, sb.ToString(), new System.Text.UTF8Encoding(true));
                return true;
            }
            catch (Exception e)
            {
                MessageBox.Show($@"CSV file: '{csvFile}'

{e}", @"Error writing csv export (file open in Excel?)");
                return false;
            }
        }

        private void AddCsvRows(System.Text.StringBuilder sb, string del,
            System.Collections.Generic.List<string> columns, string specName,
            System.Collections.Generic.IEnumerable<SpecHierarchy> hierarchies, int level)
        {
            foreach (var h in hierarchies)
            {
                if (h.Object != null)
                {
                    var values = new System.Collections.Generic.Dictionary<string, string>();
                    foreach (var v in h.Object.Values)
                    {
                        string name = v.AttributeDefinition?.LongName;
                        if (name != null && !values.ContainsKey(name)) values[name] = CsvValue(v);
                    }
                    var row = new[] {specName, level.ToString()}
                        .Concat(columns.Select(c => values.TryGetValue(c, out var val) ? val : ""));
                    sb.Append(String.Join(del, row.Select(CsvQuote))).Append("\r\n");
                }
                AddCsvRows(sb, del, columns, specName, h.Children, level + 1);
            }
        }

        private static string CsvValue(AttributeValue v)
        {
            object o;
            try
            {
                o = v.ObjectValue;
            }
            catch
            {
                return "";
            }
            if (o == null) return "";
            if (o is System.Collections.Generic.List<EnumValue> enums)
                return String.Join(", ", enums.Select(x => x.LongName));
            if (o is DateTime date) return date.ToString("yyyy-MM-dd HH:mm:ss");
            if (o is bool b) return b ? "true" : "false";
            if (v is AttributeValueXHTML) return XhtmlToText(o.ToString());
            return o.ToString();
        }

        private static string XhtmlToText(string xhtml)
        {
            string text = Regex.Replace(xhtml, @"<(\w+:)?br\s*/?>", "\n", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"</(\w+:)?(p|div|li|h\d)>", "\n", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"<(\w+:)?li[^>]*>", "- ", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"<[^>]+>", "");
            text = System.Net.WebUtility.HtmlDecode(text);
            text = Regex.Replace(text, @"[ \t]+\n", "\n");
            text = Regex.Replace(text, @"\n{3,}", "\n\n");
            return text.Trim();
        }

        private static string CsvQuote(string value)
        {
            return $"\"{(value ?? "").Replace("\"", "\"\"")}\"";
        }

        /// <summary>
        /// Create Specification with hierarchy
        /// </summary>
        private bool CreateSpecHierarchy()
        {
            AddPackageToHierarchy(_moduleSpecification.Children, Pkg);
            return true;
        }

        /// <summary>
        /// Add the content of a package to the hierarchy:
        /// - With 'IncludeSubPackages': sub-packages first (like the EA Project Browser), as heading with their content below
        /// - Elements of the package (nested elements below their parent)
        /// </summary>
        /// <param name="children"></param>
        /// <param name="pkg"></param>
        private void AddPackageToHierarchy(System.Collections.Generic.ICollection<SpecHierarchy> children, EA.Package pkg)
        {
            if (Settings.IncludeSubPackages)
            {
                foreach (EA.Package subPkg in pkg.Packages)
                {
                    EA.Element pkgElement = subPkg.Element;
                    if (pkgElement == null) continue;
                    var specHierarchy = MakeSpecHierarchy(pkgElement);
                    children.Add(specHierarchy);
                    AddPackageToHierarchy(specHierarchy.Children, subPkg);
                }
            }
            foreach (EA.Element el in pkg.Elements)
            {
                if (_skipNoteElements && el.Type == "Note") continue;
                var specHierarchy = MakeSpecHierarchy(el);
                children.Add(specHierarchy);
                foreach (EA.Element child in el.Elements)
                {
                    CreateSpecHierarchyElement(specHierarchy, child);
                }
            }
        }

        /// <summary>
        /// Make a SpecHierarchy for the element (or package element) with the link to its SpecObject
        /// </summary>
        /// <param name="el"></param>
        /// <returns></returns>
        private SpecHierarchy MakeSpecHierarchy(EA.Element el)
        {
            var idSpecObject = ReqIfUtils.MakeReqIfId(ReqIfUtils.ReqIfIdType.SpecObject, ReqIfUtils.IdFromGuid(el.ElementGUID));
            var specObject = _reqIfContent.SpecObjects.SingleOrDefault(x => x.Identifier == idSpecObject);
            return new SpecHierarchy()
            {
                Identifier = ReqIfUtils.MakeReqIfId(ReqIfUtils.ReqIfIdType.SpecHierarchy, ReqIfUtils.IdFromGuid(el.ElementGUID)),
                LastChange = el.Modified,
                LongName = el.Name,
                Object = specObject
            };
        }

        /// <summary>
        /// Collect the PackageID of the package and, if wanted, of all its sub-packages (recursive)
        /// </summary>
        /// <param name="pkg"></param>
        /// <param name="ids"></param>
        /// <param name="recursive"></param>
        private static void CollectPackageIds(EA.Package pkg, System.Collections.Generic.List<long> ids, bool recursive)
        {
            if (ids.Contains(pkg.PackageID)) return;
            ids.Add(pkg.PackageID);
            if (!recursive) return;
            foreach (EA.Package subPkg in pkg.Packages)
            {
                CollectPackageIds(subPkg, ids, true);
            }
        }
        /// <summary>
        /// Create a SpecHierarchy with the link to the SpecObject
        /// </summary>
        /// <param name="parent"></param>
        /// <param name="el"></param>
        private void CreateSpecHierarchyElement(SpecHierarchy parent, EA.Element el)
        {
            if (_skipNoteElements && el.Type == "Note") return;
            var idSpecObject = ReqIfUtils.MakeReqIfId(ReqIfUtils.ReqIfIdType.SpecObject, ReqIfUtils.IdFromGuid(el.ElementGUID));
            //var idSpecObject = ReqI$"specObj{ReqIfUtils.IdFromGuid(el.ElementGUID)}";
            var specObject = _reqIfContent.SpecObjects.SingleOrDefault(x => x.Identifier == idSpecObject);
            var specHierarchy = new SpecHierarchy()
            {
                Identifier = ReqIfUtils.MakeReqIfId(ReqIfUtils.ReqIfIdType.SpecHierarchy, ReqIfUtils.IdFromGuid(el.ElementGUID)),
                LastChange = el.Modified,
                LongName = el.Name,
                Object = specObject
            };
            parent.Children.Add(specHierarchy);
            foreach (EA.Element child in el.Elements)
            {
                CreateSpecHierarchyElement(specHierarchy, child);
            }

        }


        /// <summary>
        /// Create SpecObject for all Requirements of Package
        /// </summary>
        private bool CreateSpecObjects()
        {
            using (var db = new EaDataModel(LinqOptions))
            {
                // Read all Requirements for module/specification
                var reqs = (from r in db.t_object
                            join pkg in db.t_package on r.Package_ID equals pkg.Package_ID
                            where _packageIds.Contains(pkg.Package_ID) &&
                                  !(_skipNoteElements && r.Object_Type == "Note")
                            from tv in db.t_objectproperties.Where(tv2 => r.Object_ID == tv2.Object_ID)
                                .DefaultIfEmpty() // <<= makes join left join
                            orderby r.ea_guid, tv.Property // ensure requirements are on a stretch

                            select new
                            {
                                Name = r.Name,
                                ModifiedOn = r.ModifiedDate,
                                CreatedOn = r.CreatedDate,
                                Author = r.Author,
                                Guid = r.ea_guid,
                                Desc = r.Note,
                                TvName = tv.Property,
                                TvValue = tv.Value,
                                TvNote = tv.Notes
                            }).ToArray();



                string currentGuid = "";
                SpecObject specObject = null;
                EA.Element el = null;
                string lastTaggedValueName = "";
                string taggedValueNameModifier = "";

                // over all requirements
                foreach (var r in reqs)
                {
                    // new Requirements
                    if (currentGuid != r.Guid)
                    {
                        lastTaggedValueName = "";
                        taggedValueNameModifier = "";
                        currentGuid = r.Guid;
                        try
                        {
                            el = Rep.GetElementByGuid(currentGuid);
                            specObject = new SpecObject
                            {
                                LongName = $"{r.Name}",
                                Identifier = ReqIfUtils.MakeReqIfId(ReqIfUtils.ReqIfIdType.SpecObject,
                                    ReqIfUtils.IdFromGuid(r.Guid)),
                                LastChange = DateTime.Now,
                                Type = _specObjectType
                            };
                            // ID
                            var attributeValueString = new AttributeValueString
                            {
                                Definition =
                                    (AttributeDefinitionString) _specObjectType.SpecAttributes.First(x =>
                                        x.GetType() == typeof(AttributeDefinitionString) &&
                                        x.LongName == "ReqIF.ForeignId"),
                                TheValue = GuidWithoutBraces(r.Guid)
                            };
                            specObject.Values.Add(attributeValueString);
                            
                            // Created on
                            var attributeValueDate = new AttributeValueDate
                            {
                                Definition =
                                    (AttributeDefinitionDate) _specObjectType.SpecAttributes.First(x =>
                                        x.GetType() == typeof(AttributeDefinitionDate) &&
                                        x.LongName == "ReqIF.ForeignCreatedOn"),
                               
                                TheValue = DateTime.TryParse(r.CreatedOn, out var createdOn) ? createdOn : DateTime.Now

                            };
                            specObject.Values.Add(attributeValueDate);

                            // Modified on
                            attributeValueDate = new AttributeValueDate
                            {
                                Definition =
                                    (AttributeDefinitionDate) _specObjectType.SpecAttributes.First(x =>
                                        x.GetType() == typeof(AttributeDefinitionDate) &&
                                        x.LongName == "ReqIF.ForeignModifiedOn"),
                                TheValue = DateTime.TryParse(r.ModifiedOn, out var modifiedOn) ? modifiedOn : DateTime.Now
                            };
                            specObject.Values.Add(attributeValueDate);
                            
                            // Created by
                            var attributeValueXhtml = new AttributeValueXHTML()
                            {
                                Definition =
                                    (AttributeDefinitionXHTML) _specObjectType.SpecAttributes.First(x =>
                                        x.GetType() == typeof(AttributeDefinitionXHTML) &&
                                        x.LongName == "ReqIF.ForeignCreatedBy"),
                                TheValue = MakeXhtmlFromString(r.Author)
                            };
                            specObject.Values.Add(attributeValueXhtml);
                            // Modified by
                            attributeValueXhtml = new AttributeValueXHTML
                            {
                                Definition =
                                    (AttributeDefinitionXHTML) _specObjectType.SpecAttributes.First(x =>
                                        x.GetType() == typeof(AttributeDefinitionXHTML) &&
                                        x.LongName == "ReqIF.ForeignModifiedBy"),
                                TheValue = MakeXhtmlFromString("not supported")
                            };
                            specObject.Values.Add(attributeValueXhtml);
                            // Attribute Name
                            if (r.Name != null)
                            {
                                attributeValueXhtml = new AttributeValueXHTML
                                {
                                    Definition =
                                        (AttributeDefinitionXHTML) _specObjectType.SpecAttributes.First(x =>
                                            x.GetType() == typeof(AttributeDefinitionXHTML) &&
                                            x.LongName == "ReqIF.Name"),
                                    TheValue = MakeXhtmlFromString(r.Name)
                                };
                                specObject.Values.Add(attributeValueXhtml);
                            }

                            // Attributes from ExportMappingList (EA properties like EA.Status, EA.Type, EA.Stereotype)
                            if (el != null)
                            {
                                foreach (string mappedName in _exportMapping.GetFields())
                                {
                                    attributeValueXhtml = new AttributeValueXHTML
                                    {
                                        Definition =
                                            (AttributeDefinitionXHTML) _specObjectType.SpecAttributes.First(x =>
                                                x.GetType() == typeof(AttributeDefinitionXHTML) &&
                                                x.LongName == mappedName),
                                        TheValue = GetMappedXhtmlValue(el, mappedName)
                                    };
                                    specObject.Values.Add(attributeValueXhtml);
                                }
                            }


                            // Attribute Text (note or Linked Document)
                            // _settings.SpecHandling
                            // - FileImportSettingsItem.SpecHandlingType.MixedMode  Preferred LinkedDocument, if no LinkedDocument then Notes
                            // - FileImportSettingsItem.SpecHandlingType.OnlyLinkedDocument
                            // - FileImportSettingsItem.SpecHandlingType.OnlyNotes

                            string rtfText = el?.GetLinkedDocument();
                            // Scenarios (Use Case) as HTML, appended to ReqIF.Text
                            string scenarioHtml = Settings.ExportScenarios && el != null ? MakeScenarioHtml(el) : "";
                            var definition = (AttributeDefinitionXHTML) _specObjectType.SpecAttributes.First(
                                x =>
                                    x.GetType() == typeof(AttributeDefinitionXHTML) && x.LongName == "ReqIF.Text");

                            // rtf text/description available and rtf export wanted (Mixed Mode or OnlyLinkedDocument)
                            if (!String.IsNullOrEmpty(rtfText) &&
                                (
                                    Settings.SpecHandling == FileImportSettingsItem.SpecHandlingType.MixedMode ||
                                    Settings.SpecHandling == FileImportSettingsItem.SpecHandlingType.OnlyLinkedDocument
                                )
                            )
                            {
                                string fileDir = Settings.EmbeddedFileStorageDictionary;
                                string xhtml = RtfToXhtml.ConvertRtfToXhtml(rtfText, fileDir, Settings.EmbeddedFilesPng);

                                // Handle embedded files of the EA-Element
                                xhtml = $@"{xhtml}
{_exportEmbeddedEaFile.MakeXhtmlForEmbeddedFiles(el)}{scenarioHtml}";

                                // Text = Linked Document
                                attributeValueXhtml = new AttributeValueXHTML
                                {
                                    Definition = definition,
                                    TheValue = MakeReqIfXhtmlFromXhtml(xhtml)
                                };
                                specObject.Values.Add(attributeValueXhtml);

                            }
                            else
                            {
                                // Check export EA Notes
                                // No rtf export and Mixed Mode or OnlyNotes
                                bool exportNotes = !String.IsNullOrEmpty(r.Desc) &&
                                    (Settings.SpecHandling == FileImportSettingsItem.SpecHandlingType.MixedMode ||
                                     Settings.SpecHandling == FileImportSettingsItem.SpecHandlingType.OnlyNotes
                                    );
                                if (exportNotes || scenarioHtml != "")
                                {
                                    // Text = notes (+ scenarios)
                                    string html = (exportNotes ? Rep.GetFormatFromField("HTML", r.Desc) : "") + scenarioHtml;
                                    attributeValueXhtml = new AttributeValueXHTML
                                    {
                                        Definition = definition,
                                        TheValue = MakeXhtmlFromHtml(html)
                                    };
                                    specObject.Values.Add(attributeValueXhtml);
                                }
                            }

                            //------------------  Values added to SpecObjects ----------------------------------------
                            // Add the SpecObject
                            _reqIfContent.SpecObjects.Add(specObject);

                            // Export all embedded element files
                            _exportEmbeddedEaFile.CopyEmbeddedFiles(el);
                        }
                        catch (Exception e)
                        {

                            MessageBox.Show($@"Req Name:  '{el?.Name}'
Req Guid: '{el?.ElementGUID}'

{e}",@"Error exporting EA Requirements to ReqIf");
                        }
                    } // new requirement


                    //--------------------------------------------------------------------------------------
                    // Add Tagged Value if defined
                    if (! String.IsNullOrWhiteSpace(r.TvName) && ! r.TvName.StartsWith("ReqIF.") &&
                        ! _exportMapping.IsWritableValue(r.TvName))
                    {
                        string tvName = r.TvName;
                        // Handle repeated tagged value name
                        // This is possible with structured Tagged Values
                        if (tvName == lastTaggedValueName)
                        {
                            taggedValueNameModifier = $"{taggedValueNameModifier}.";
                            tvName = $"{tvName}{taggedValueNameModifier}";
                            lastTaggedValueName = tvName;
                        } else lastTaggedValueName = tvName;

                        // Check if tagged value is enumeration
                        var dataTypeEnumeration =
                            (DatatypeDefinitionEnumeration) _reqIfContent.DataTypes.FirstOrDefault(x =>
                                x.GetType() == typeof(DatatypeDefinitionEnumeration)
                                && x.LongName == tvName);
                        if (dataTypeEnumeration == null)
                        {
                            var attributeValueXhtml = new AttributeValueXHTML
                            {
                                Definition =
                                    (AttributeDefinitionXHTML) _specObjectType.SpecAttributes.FirstOrDefault(x =>
                                        x.GetType() == typeof(AttributeDefinitionXHTML) && x.LongName == tvName),
                                TheValue = MakeXhtmlFromEaNotes(Rep, ReqIfUtils.GetEaTaggedValue(r.TvValue, r.TvNote))
                            };
                            specObject.Values.Add(attributeValueXhtml);
                        }
                        else
                        {
                            var attributeDefinitionEnumeration =
                                (AttributeDefinitionEnumeration) _specObjectType.SpecAttributes.FirstOrDefault(x =>
                                    x.GetType() == typeof(AttributeDefinitionEnumeration) && x.LongName == tvName);
                            var attributeValueEnumeration = new AttributeValueEnumeration
                            {
                                Definition = attributeDefinitionEnumeration

                            };
                            if (!SetReqIfEnumValue(attributeValueEnumeration, r.TvValue, el)) return false;
                            specObject.Values.Add(attributeValueEnumeration);

                        }
                    }
                }
            }

            return true;

        }

        /// <summary>
        /// Add ReqIF Attribute Value to ReqIF Specification
        /// </summary>
        /// <param name="specObject"></param>
        /// <param name="attrValue"></param>
        /// <param name="errorText"></param>
        /// <returns></returns>
        private bool AddAttributeToSpecObj(SpecObject specObject, AttributeValue attrValue, EA.Element el=null, string errorText="")
        {
            try
            {
                specObject.Values.Add(attrValue);
            }
            catch (Exception )
            {
                MessageBox.Show($@"{errorText}

Req:      {el?.Name};
Req GUID: {el?.ElementGUID}
Type:     {attrValue.GetType()}
Value:    {attrValue}
", @"Error adding Attribute value to SpecObj");
            }

            return true;
        }


        /// <summary>
        /// Make XHTML from a EA notes. It inserts the xhtml namespace and handles special characters
        /// </summary>
        /// <param name="rep"></param>
        /// <param name="stringText"></param>
        /// <returns></returns>
        private static string MakeXhtmlFromEaNotes(EA.Repository rep, string stringText)
        {
            string stringHtml = rep.GetFormatFromField("HTML", stringText);
            return MakeXhtmlFromHtml(stringHtml);
        }



        /// <summary>
        /// Add Data Types for Notes (Enums, Checklist)
        /// </summary>
        /// <param name="reqIf"></param>
        /// <param name="pkg"></param>
        // ReSharper disable once UnusedParameter.Local
        private void AddDatatypesForPackage(ReqIF reqIf, EA.Package pkg)
        {
            using (var db = new EaDataModel(LinqOptions))
            {
                var tvProperties = (from tv in db.t_objectproperties
                    join o in db.t_object on tv.Object_ID equals o.Object_ID
                    join p in db.t_package on o.Package_ID equals p.Package_ID
                    join tvType in db.t_propertytypes on tv.Property equals tvType.Property
                    where _packageIds.Contains(p.Package_ID) && tvType.Notes.Contains("Values=") && 
                                                           (tvType.Notes.Contains("Type=Enum") || tvType.Notes.Contains("Type=CheckList"))
                    orderby tvType.Property
                    group tvType by new { Property = tvType.Property, Notes = tvType.Notes.Substring(0) } into grp  //, tvType.Notes
                    select new { grp.Key.Property, grp.Key.Notes }).Distinct();

                // make ReqIF DataTypes
                foreach (var tv in tvProperties)
                {
                    // don't use ReqIF standard Attributes which starts by 'ReqIF.'
                    if (tv.Property.StartsWith("ReqIF."))
                    {
                        if (_errorTvReported) continue;
                        MessageBox.Show($@"Tagged Value skipped: '{tv.Property}'

All EA Tagged Values starting with 'ReqIF.' are skipped!", @"Don't use ReqIF standard attributes which starts with 'ReqIF.'");
                        _errorTvReported = true;
                    }

                    var propertyValue = tv.Property.Trim();
                    // Check if Enumeration-Datatype already exists
                    var dataTypeEnumeration = (DatatypeDefinitionEnumeration)_reqIfContent.DataTypes.FirstOrDefault(x => x.GetType() == typeof(DatatypeDefinitionEnumeration)
                                                                                                                        && x.LongName == propertyValue);
                    if (dataTypeEnumeration != null) continue;
                    string idEnum = ReqIfUtils.MakeReqIfId(ReqIfUtils.ReqIfIdType.DataType, pkg.Name, propertyValue);
                    var datatypeDefinitionEnumeration = new DatatypeDefinitionEnumeration
                    {
                        Description = $"{tv.Notes}",
                        Identifier = idEnum,
                        LastChange = DateTime.Now,
                        LongName = $"{propertyValue}"
                        
                    };
                    // get enumeration values
                    Regex rx = new Regex("Values=([^;]*);");
                    Match match = rx.Match(tv.Notes);
                    if (match.Success)
                    {
                        int index = 0;
                        foreach (var value in match.Groups[1].Value.Split(','))
                        {
                            index += 1;
                            var embeddedValue = new EmbeddedValue
                            {
                                Key = index,
                                OtherContent = value.Trim(),

                            };
                            var enumValue = new EnumValue
                            {
                                Identifier = ReqIfUtils.MakeReqIfId(ReqIfUtils.ReqIfIdType.EnumValue, propertyValue, index.ToString()),
                                LastChange = DateTime.Now,
                                LongName = value.Trim(),
                                Properties = embeddedValue
                            };
                            // Add enum value
                            datatypeDefinitionEnumeration.SpecifiedValues.Add(enumValue);
                        }
                        
                    }
                    _reqIfContent.DataTypes.Add(datatypeDefinitionEnumeration);

                }

            }

        }

        /// <summary>
        /// Check if Attribute ID exists
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        private bool IsReqIfDataTypeExists( string id)
        {
            return _specObjectType.SpecAttributes.Any(x =>
                x.Identifier == id);
        }


        /// <summary>
        /// Write the ReqIF moduleSpecification, DataTypes, SpecObjecttype for the pkg / module specification
        /// </summary>
        /// <param name="pkg"></param>
        private void WriteModuleSpecification([NotNull]EA.Package pkg)
        {
            // Module specification
            _moduleSpecification = new Specification
            {
                
                Description = $"Module specification of Package '{pkg.Name}', GUID={pkg.PackageGUID}",
                Identifier = ReqIfUtils.MakeReqIfId(ReqIfUtils.ReqIfIdType.Specification, ReqIfUtils.IdFromGuid(pkg.PackageGUID)),
                LastChange = DateTime.Now,
                LongName = $"Module {pkg.Name}",
                Type = _specificationTypeModule
            };
           _reqIfContent.Specifications.Add(_moduleSpecification);

           // Add Module Specification
            AddAttributesModuleSpecification(_moduleSpecification, pkg);
            // Add dataTypes for packages (enums)
            AddDatatypesForPackage(ReqIfDeserialized, pkg);
            // AddSpecObj type for package/module
            AddSpeObjectTypeForModule(ReqIfDeserialized, pkg);

        }

        /// <summary>
        /// Add Spec object type for module/package
        /// </summary>
        /// <param name="reqIf"></param>
        /// <param name="pkg"></param>
        // ReSharper disable once UnusedParameter.Local
        private void AddSpeObjectTypeForModule(ReqIF reqIf, EA.Package pkg)
        {
            // SpecObjType of package/module
            _specObjectType = new SpecObjectType
            {
                LongName = $"{pkg.Name}",
                Identifier = $"specObjT{ReqIfUtils.IdFromGuid(pkg.PackageGUID)}",
                LastChange = DateTime.Now
               };

            
            // Add Standard Attributes
            // 
            var attributeDefinitionXhtml = new AttributeDefinitionXHTML
            {
                LongName = "ReqIF.Name",
                Identifier = ReqIfUtils.MakeReqIfId(ReqIfUtils.ReqIfIdType.Attribute, pkg.Name, "ReqIF.Name"),
                LastChange = DateTime.Now,
                Type = (DatatypeDefinitionXHTML)_reqIfContent.DataTypes.SingleOrDefault(x =>
                    x.GetType() == typeof(DatatypeDefinitionXHTML))
            };
            _specObjectType.SpecAttributes.Add(attributeDefinitionXhtml);
            attributeDefinitionXhtml = new AttributeDefinitionXHTML
            {
                LongName = "ReqIF.ChapterName",
                Identifier = ReqIfUtils.MakeReqIfId(ReqIfUtils.ReqIfIdType.Attribute, pkg.Name, "ReqIF.ChapterName"),
                LastChange = DateTime.Now,
                Type = (DatatypeDefinitionXHTML)_reqIfContent.DataTypes.SingleOrDefault(x =>
                    x.GetType() == typeof(DatatypeDefinitionXHTML))
            };
            _specObjectType.SpecAttributes.Add(attributeDefinitionXhtml);
            attributeDefinitionXhtml = new AttributeDefinitionXHTML
            {
                LongName = "ReqIF.Text",
                Identifier = ReqIfUtils.MakeReqIfId(ReqIfUtils.ReqIfIdType.Attribute, pkg.Name, "ReqIF.Text"),
                LastChange = DateTime.Now,
                Type = (DatatypeDefinitionXHTML)_reqIfContent.DataTypes.SingleOrDefault(x =>
                    x.GetType() == typeof(DatatypeDefinitionXHTML))
            };
           _specObjectType.SpecAttributes.Add(attributeDefinitionXhtml);

            attributeDefinitionXhtml = new AttributeDefinitionXHTML
            {
                LongName = "ReqIF.ForeignModifiedBy",
                Identifier = ReqIfUtils.MakeReqIfId(ReqIfUtils.ReqIfIdType.Attribute, pkg.Name, "ReqIF.ModifiedBy"),
                LastChange = DateTime.Now,
                Type = (DatatypeDefinitionXHTML)_reqIfContent.DataTypes.SingleOrDefault(x =>
                    x.GetType() == typeof(DatatypeDefinitionXHTML))
            };
            _specObjectType.SpecAttributes.Add(attributeDefinitionXhtml);

            attributeDefinitionXhtml = new AttributeDefinitionXHTML
            {
                LongName = "ReqIF.ForeignCreatedBy",
                Identifier = ReqIfUtils.MakeReqIfId(ReqIfUtils.ReqIfIdType.Attribute, pkg.Name, "ReqIF.CreatedBy"),
                LastChange = DateTime.Now,
                Type = (DatatypeDefinitionXHTML)_reqIfContent.DataTypes.SingleOrDefault(x =>
                    x.GetType() == typeof(DatatypeDefinitionXHTML))
            };
            _specObjectType.SpecAttributes.Add(attributeDefinitionXhtml);

            var attributeDefinitionDate = new AttributeDefinitionDate
            {
                LongName = "ReqIF.ForeignCreatedOn",
                Identifier = ReqIfUtils.MakeReqIfId(ReqIfUtils.ReqIfIdType.Attribute, pkg.Name, "ReqIF.CreatedOn"),
                LastChange = DateTime.Now,
                Type = (DatatypeDefinitionDate)_reqIfContent.DataTypes.SingleOrDefault(x =>
                    x.GetType() == typeof(DatatypeDefinitionDate))
            };
            _specObjectType.SpecAttributes.Add(attributeDefinitionDate);


            attributeDefinitionDate = new AttributeDefinitionDate
            {
                LongName = "ReqIF.ForeignModifiedOn",
                Identifier = ReqIfUtils.MakeReqIfId(ReqIfUtils.ReqIfIdType.Attribute, pkg.Name, "ReqIF.ModifiedOn"),
                LastChange = DateTime.Now,
                Type = (DatatypeDefinitionDate)_reqIfContent.DataTypes.SingleOrDefault(x =>
                    x.GetType() == typeof(DatatypeDefinitionDate))
            };
            _specObjectType.SpecAttributes.Add(attributeDefinitionDate);

            var attributeDefinitionBool = new AttributeDefinitionBoolean
            {
                LongName = "ReqIF.ForeignDeleted",
                Identifier = ReqIfUtils.MakeReqIfId(ReqIfUtils.ReqIfIdType.Attribute, pkg.Name, "ReqIF.ForeignDeleted"),
                LastChange = DateTime.Now,
                Type = (DatatypeDefinitionBoolean)_reqIfContent.DataTypes.SingleOrDefault(x =>
                    x.GetType() == typeof(DatatypeDefinitionBoolean))
            };
            _specObjectType.SpecAttributes.Add(attributeDefinitionBool);

            var attributeDefinitionString = new AttributeDefinitionString
            {
                LongName = "ReqIF.ForeignId",
                Identifier = ReqIfUtils.MakeReqIfId(ReqIfUtils.ReqIfIdType.Attribute, pkg.Name, "ReqIF.ForeignId"),
                LastChange = DateTime.Now,
                Type = (DatatypeDefinitionString)_reqIfContent.DataTypes.SingleOrDefault(x =>
                    x.GetType() == typeof(DatatypeDefinitionString))
            };
            _specObjectType.SpecAttributes.Add(attributeDefinitionString);

            // Attributes from ExportMappingList (EA properties like EA.Status, EA.Type, EA.Stereotype)
            foreach (string mappedName in _exportMapping.GetFields())
            {
                attributeDefinitionXhtml = new AttributeDefinitionXHTML
                {
                    LongName = mappedName,
                    Identifier = ReqIfUtils.MakeReqIfId(ReqIfUtils.ReqIfIdType.Attribute, pkg.Name, mappedName),
                    LastChange = DateTime.Now,
                    Type = (DatatypeDefinitionXHTML)_reqIfContent.DataTypes.SingleOrDefault(x =>
                        x.GetType() == typeof(DatatypeDefinitionXHTML))
                };
                _specObjectType.SpecAttributes.Add(attributeDefinitionXhtml);
            }


            using (var db = new EaDataModel(LinqOptions))
            {
                // get all TVs
                var tvs = (from tv in db.t_objectproperties
                join o in db.t_object on tv.Object_ID equals o.Object_ID
                join p in db.t_package on o.Package_ID equals p.Package_ID
                where _packageIds.Contains(p.Package_ID)
                orderby tv.Property
                //group tv by new { tv.Property, tv.PropertyID} into grp
                select tv.Property).Distinct();

                // add attributes of the tagged values
                foreach (var tvName in tvs)
                {

                    var dataTypeEnumeration = (DatatypeDefinitionEnumeration)_reqIfContent.DataTypes.SingleOrDefault(x => x.GetType() == typeof(DatatypeDefinitionEnumeration)
                                                                                                                        && x.LongName == tvName);
                    // no ReqIF Standard-Attributes as Tagged Values 
                    if (tvName.StartsWith("ReqIF.")) continue;
                    // EA property from ExportMappingList has priority over a Tagged Value with the same name
                    if (_exportMapping.IsWritableValue(tvName)) continue;
                    if (dataTypeEnumeration == null)
                    {

                            attributeDefinitionXhtml = new AttributeDefinitionXHTML
                            {
                                LongName = $"{tvName}",
                                Identifier = ReqIfUtils.MakeReqIfId(ReqIfUtils.ReqIfIdType.Attribute, pkg.Name, tvName),
                                LastChange = DateTime.Now,
                                Type = (DatatypeDefinitionXHTML) _reqIfContent.DataTypes.SingleOrDefault(x =>
                                    x.GetType() == typeof(DatatypeDefinitionXHTML))
                            };
                            _specObjectType.SpecAttributes.Add(attributeDefinitionXhtml);

                    }
                    else
                    { // Enumeration
                        
                       
                       var attributeDefinitionEnumeration = new AttributeDefinitionEnumeration
                            {
                                LongName = $"{tvName}",
                                Identifier = ReqIfUtils.MakeReqIfId(ReqIfUtils.ReqIfIdType.Attribute, pkg.Name, tvName),
                                LastChange = DateTime.Now,
                                Type = dataTypeEnumeration
                            };
                        if (dataTypeEnumeration.Description.Contains("Type=CheckList"))
                            attributeDefinitionEnumeration.IsMultiValued = true;
                        _specObjectType.SpecAttributes.Add(attributeDefinitionEnumeration);

                    }
                }
            }
            // Add Attrributes to SpecObjType
           _reqIfContent.SpecTypes.Add(_specObjectType);


        }

        /// <summary>
        /// Adds the ReqIF header if the *.reqifz file doesn't exists. It also adds the default definitions:
        /// - Core Data Types
        /// - SpecificationType Module
        /// If the *.reqifz file exists it reads and deserialize it. 
        /// </summary>
        /// <param name="file"></param>
        /// <param name="newFile"></param>
        /// <returns></returns>
        private ReqIF AddHeader(string file, bool newFile = true)
        {
            if (newFile && File.Exists(file)) DirFiles.FileDelete(file);
            if (File.Exists(file))
            {
                // decompress reqif file and its embedded files
                string[] importReqIfFile = Decompress(file);
                if (String.IsNullOrWhiteSpace(importReqIfFile[0])) return null;

               // deserialize file
               return DeSerializeReqIf(file,Settings.ValidateReqIF);
              
            }
            else
            {
                // Create new ReqIF file
                var reqIf = new ReqIF
                {
                    Lang = "en"
                };
                var header = new ReqIFHeader()
                {
                    Title = $"{Settings.Name}",
                    CreationTime = DateTime.Now,
                    Identifier = ReqIfUtils.MakeReqIfId(ReqIfUtils.ReqIfIdType.ReqIfHeader),
                    RepositoryId = Rep.ProjectGUID,
                    ReqIFToolId = $"hoReverse V{Assembly.GetExecutingAssembly().GetName().Version}",
                    ReqIFVersion = "1.0",
                    SourceToolId = $"SPARX EA Lib={Rep.LibraryVersion}",
                    Comment = $"Export EA from {Rep.ConnectionString}"
                };
                reqIf.TheHeader.Add(header);

                // Content
                _reqIfContent = new ReqIFContent();
                reqIf.CoreContent.Add(_reqIfContent);

                // Create basic data types like boolean
                AddCoreDataTypes(reqIf);
                // Create basic attribute types like Name, Description, Changed, Modified, By
                _specificationTypeModule = AddSpecificationTypeModule(reqIf);
                return reqIf;


            }
        }
        /// <summary>
        /// Create Core Data Types:
        /// - Boolean
        /// - DateTime
        /// - String
        /// - XHTML
        /// </summary>
        // ReSharper disable once UnusedParameter.Local
        private void AddCoreDataTypes(ReqIF reqIf)
        {
            var datatypeDefinitionBoolean =
                new DatatypeDefinitionBoolean
                {
                    Description = "boolean data type definition",
                    Identifier = ReqIfUtils.MakeReqIfId(ReqIfUtils.ReqIfIdType.DataType, "boolean"),
                    LastChange = DateTime.Now,
                    LongName = "a boolean"
                };
           _reqIfContent.DataTypes.Add(datatypeDefinitionBoolean);

            var datatypeDefinitionDate = new DatatypeDefinitionDate
            {
                Description = "date data type definition",
                Identifier = ReqIfUtils.MakeReqIfId(ReqIfUtils.ReqIfIdType.DataType,"DateTime"),
                LastChange = DateTime.Now,
                LongName = "a date"
            };
           _reqIfContent.DataTypes.Add(datatypeDefinitionDate);

            var datatypeDefinitionInteger =
                new DatatypeDefinitionInteger
                {
                    Description = "integer data type definition",
                    Identifier = ReqIfUtils.MakeReqIfId(ReqIfUtils.ReqIfIdType.DataType, "Integer"),
                    LastChange = DateTime.Now,
                    LongName = "an integer",
                    Min = -2147483648,
                    Max = 2147483647
                };
           _reqIfContent.DataTypes.Add(datatypeDefinitionInteger);

            var datatypeDefinitionString = new DatatypeDefinitionString
            {
                Description = "string data type definition",
                Identifier = ReqIfUtils.MakeReqIfId(ReqIfUtils.ReqIfIdType.DataType, "String"),
                LastChange = DateTime.Now,
                MaxLength = 32000,
                LongName = "a string"
            };
           _reqIfContent.DataTypes.Add(datatypeDefinitionString);

            var datatypeDefinitionXhtml = new DatatypeDefinitionXHTML
            {
                Description = "string data type definition",
                Identifier = "xhtml",
                LastChange = DateTime.Now,
                LongName = "a string"
            };
           _reqIfContent.DataTypes.Add(datatypeDefinitionXhtml);

        }
        /// <summary>
        /// Create and add the module <see cref="SpecificationType"/> with attribute definitions
        /// </summary>
        // ReSharper disable once UnusedParameter.Local
        private SpecificationType AddSpecificationTypeModule(ReqIF reqIf)
        {
            var specificationTypeModule = new SpecificationType
            {
                LongName = "Modules/Packages",
                Identifier = ReqIfUtils.MakeReqIfId(ReqIfUtils.ReqIfIdType.DataType, _subModuleIndex.ToString()),
                LastChange = DateTime.Now

            };

            AddAttributeDefinitionsToModuleSpecificationType(specificationTypeModule);
           _reqIfContent.SpecTypes.Add(specificationTypeModule);
            return specificationTypeModule;
        }


        /// <summary>
        /// Add Attribute definitions to SpecificationType of Modules
        /// - Name
        /// - Description
        /// - Created, Modified, by,..
        /// </summary>
        /// <param name="specType"></param>
        private void AddAttributeDefinitionsToModuleSpecificationType(SpecType specType)
        {
            var attributeDefinitionReqIfName = new AttributeDefinitionXHTML
            {
                LongName = "ReqIF.Name",
                Identifier = "specification-reqif-name",
                LastChange = DateTime.Now,
                Type = (DatatypeDefinitionXHTML)_reqIfContent.DataTypes.SingleOrDefault(x =>
                    x.GetType() == typeof(DatatypeDefinitionXHTML))
            };
            specType.SpecAttributes.Add(attributeDefinitionReqIfName);

            var attributeDefinitionReqIfChapterName = new AttributeDefinitionXHTML
            {
                LongName = "ReqIF.ChapterName",
                Identifier = "specification-reqif-chapter-name",
                LastChange = DateTime.Now,
                Type = (DatatypeDefinitionXHTML)_reqIfContent.DataTypes.SingleOrDefault(x =>
                    x.GetType() == typeof(DatatypeDefinitionXHTML))
            };
            specType.SpecAttributes.Add(attributeDefinitionReqIfChapterName);

            var attributeDefinitionReqIfDescription = new AttributeDefinitionXHTML
            {
                LongName = "ReqIF.Description",
                Identifier = "specification-reqif-description",
                LastChange = DateTime.Now,
                Type = (DatatypeDefinitionXHTML)_reqIfContent.DataTypes.SingleOrDefault(x =>
                    x.GetType() == typeof(DatatypeDefinitionXHTML))
            };
            specType.SpecAttributes.Add(attributeDefinitionReqIfDescription);

            var attributeDefinitionReqICreatedBy = new AttributeDefinitionXHTML
            {
                LongName = "ReqIF.ForeignCreatedBy",
                Identifier = "specification-reqif-createdby",
                LastChange = DateTime.Now,
                Type = (DatatypeDefinitionXHTML)_reqIfContent.DataTypes.SingleOrDefault(x =>
                    x.GetType() == typeof(DatatypeDefinitionXHTML))
            };
            specType.SpecAttributes.Add(attributeDefinitionReqICreatedBy);
            var attributeDefinitionReqIModifiedBy = new AttributeDefinitionXHTML
            {
                LongName = "ReqIF.ForeignModifiedBy",
                Identifier = "specification-reqif-modifiedby",
                LastChange = DateTime.Now,
                Type = (DatatypeDefinitionXHTML)_reqIfContent.DataTypes.SingleOrDefault(x =>
                    x.GetType() == typeof(DatatypeDefinitionXHTML))
            };
            specType.SpecAttributes.Add(attributeDefinitionReqIModifiedBy);

            var attributeDefinitionReqIModifiedOn = new AttributeDefinitionDate()
            {
                LongName = "ReqIF.ForeignModifiedOn",
                Identifier = "specification-reqif-modified-on",
                LastChange = DateTime.Now,
                Type = (DatatypeDefinitionDate)_reqIfContent.DataTypes.SingleOrDefault(x =>
                    x.GetType() == typeof(DatatypeDefinitionDate))
            };
            specType.SpecAttributes.Add(attributeDefinitionReqIModifiedOn);

            var attributeDefinitionReqCreatedOn = new AttributeDefinitionDate()
            {
                LongName = "ReqIF.ForeignCreatedOn",
                Identifier = "specification-reqif-created-on",
                LastChange = DateTime.Now,
                Type = (DatatypeDefinitionDate)_reqIfContent.DataTypes.SingleOrDefault(x =>
                    x.GetType() == typeof(DatatypeDefinitionDate))
            };
            specType.SpecAttributes.Add(attributeDefinitionReqCreatedOn);


            var attributeDefinitionReqPrefix = new AttributeDefinitionXHTML()
            {
                LongName = "ReqIF.Prefix",
                Identifier = "specification-reqif-prefix",
                LastChange = DateTime.Now,
                Type = (DatatypeDefinitionXHTML)_reqIfContent.DataTypes.SingleOrDefault(x =>
                    x.GetType() == typeof(DatatypeDefinitionXHTML))
            };
            specType.SpecAttributes.Add(attributeDefinitionReqPrefix);
            
        }


        /// <summary>
        /// Create and add the module specifications <see cref="SpecificationType"/> with attribute definitions
        /// </summary>
        private void AddAttributesModuleSpecification(SpecElementWithAttributes specElementWithAttributes,  EA.Package pkg)
        {
            EA.Element elPkg = Rep.GetElementByGuid(pkg.PackageGUID);
            SpecType specType = specElementWithAttributes.SpecType;

            // Name
            var attributeValueXhtml = new AttributeValueXHTML
            {
                Definition = (AttributeDefinitionXHTML) specType.SpecAttributes.SingleOrDefault(x =>
                    x.GetType() == typeof(AttributeDefinitionXHTML) && x.Identifier == "specification-reqif-name"),
                TheValue = MakeXhtmlFromEaNotes(Rep, pkg.Name)
            };
            specElementWithAttributes.Values.Add(attributeValueXhtml);

            // Description
            attributeValueXhtml = new AttributeValueXHTML
            {
                Definition = (AttributeDefinitionXHTML) specType.SpecAttributes.SingleOrDefault(x =>
                    x.GetType() == typeof(AttributeDefinitionXHTML) &&
                    x.Identifier == "specification-reqif-description"),
                    TheValue = MakeXhtmlFromEaNotes(Rep, pkg.Notes)
            };
            specElementWithAttributes.Values.Add(attributeValueXhtml);


            // Prefix
            attributeValueXhtml = new AttributeValueXHTML
            {
                Definition = (AttributeDefinitionXHTML)specType.SpecAttributes.SingleOrDefault(x =>
                    x.GetType() == typeof(AttributeDefinitionXHTML) &&
                    x.Identifier == "specification-reqif-prefix"),
                TheValue = MakeXhtmlFromString("")
            };
            specElementWithAttributes.Values.Add(attributeValueXhtml);

            // CreatedBy
            attributeValueXhtml = new AttributeValueXHTML
            {
                Definition = (AttributeDefinitionXHTML)specType.SpecAttributes.SingleOrDefault(x =>
                    x.GetType() == typeof(AttributeDefinitionXHTML) &&
                    x.Identifier == "specification-reqif-createdby"),
                TheValue = MakeXhtmlFromString(elPkg.Author)
            };
            specElementWithAttributes.Values.Add(attributeValueXhtml);

            // CreatedOn
            var attributeValueDate = new AttributeValueDate()
            {
                Definition = (AttributeDefinitionDate)specType.SpecAttributes.SingleOrDefault(x =>
                    x.GetType() == typeof(AttributeDefinitionDate) &&
                    x.Identifier == "specification-reqif-created-on"),
                TheValue = elPkg.Created
            };
            specElementWithAttributes.Values.Add(attributeValueDate);

            // ModifiedOn
            attributeValueDate = new AttributeValueDate()
            {
                Definition = (AttributeDefinitionDate)specType.SpecAttributes.SingleOrDefault(x =>
                    x.GetType() == typeof(AttributeDefinitionDate) &&
                    x.Identifier == "specification-reqif-modified-on"),
                TheValue = elPkg.Created
            };
            specElementWithAttributes.Values.Add(attributeValueDate);

            // ModifiedBy
            attributeValueXhtml = new AttributeValueXHTML
            {
                Definition = (AttributeDefinitionXHTML)specType.SpecAttributes.SingleOrDefault(x =>
                    x.GetType() == typeof(AttributeDefinitionXHTML) &&
                    x.Identifier == "specification-reqif-modifiedby"),
                TheValue = MakeXhtmlFromString("Not supported")
            };
            specElementWithAttributes.Values.Add(attributeValueXhtml);

        }
        /// <summary>
        /// Make ReqIF XHTML from a XHTML. In essence add XHTML namespace
        /// </summary>
        /// <param name="stringText"></param>
        /// <returns></returns>
        private static string MakeReqIfXhtmlFromXhtml(string stringText)
        {
            stringText = LimitReqIfXhtml(stringText);
            return AddXtmlNameSpace(stringText);

        }


    }
}
