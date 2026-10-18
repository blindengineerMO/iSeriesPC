using System.Text.RegularExpressions;
using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Rpg.Parsing;

public static partial class RpgCompiler
{
    // Metadata is resolved independently of storage/layout binding. All scopes share
    // identities and a dependency stack; none of these compiler callbacks survive Run.
    private sealed class ForwardDeclarationTypes
    {
        private sealed class State
        {
            internal readonly Dictionary<(int, string), object> Identities = new();
            internal readonly List<Node> Stack = new();
        }
        private sealed class Node(string name, string text, int line, string kind = "field", Owner? owner = null)
        {
            internal readonly string Name = name, Text = text, Kind = kind;
            internal readonly int Line = line;
            internal readonly Owner? Owner = owner;
            internal bool Resolved;
            internal RpgField? Value;
        }
        private sealed class Owner(string name, string text, int line)
        {
            internal readonly string Name = name, Text = text;
            internal readonly int Line = line;
            internal readonly Dictionary<string, Node> Members = new(StringComparer.OrdinalIgnoreCase);
            internal readonly Node Root = new(name, text, line, "structure");
            internal bool InheritInitialization;
            internal string[] Image = [], InitializedImage = [];
            internal RpgDataStructure? Value, InitializedValue;
        }
        private RpgProgram _program;
        private readonly List<string> _source;
        private readonly bool _free;
        private readonly CancellationToken _cancellation;
        private readonly ForwardDeclarationTypes? _parent;
        private readonly State _state;
        private readonly Dictionary<string, Node> _values = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Node> _returns = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Owner> _structures = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, Owner> _owners = new();
        private readonly Dictionary<string, ForwardDeclarationTypes> _children = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _entryNames = new();
        private Node? _entry;
        private bool _indexed;

        internal ForwardDeclarationTypes(RpgProgram program, List<string> source, bool free, CancellationToken cancellation,
            ForwardDeclarationTypes? parent = null)
        { _program = program; _source = new(source); _free = free; _cancellation = cancellation; _parent = parent; _state = parent?._state ?? new(); }
        internal void Attach(RpgProgram program)
        {
            _program = program;
            program.ForwardTypeLookup = Target;
            program.ForwardStructureLookup = Template;
            program.DeclarationIdentityLookup = Identity;
        }
        internal void Detach()
        { _program.ForwardTypeLookup = null; _program.ForwardStructureLookup = null; _program.DeclarationIdentityLookup = null; }
        internal void Child(string name, List<string> lines)
        {
            var scope = new RpgProgram { Name = _program.Name, Library = _program.Library, NativeProcedureName = name, GlobalProgram = _program };
            _children.Add(name, new(scope, lines, _free, _cancellation, this));
        }
        internal ForwardDeclarationTypes Child(string name) => _children[name];
        private object Identity(int line, string name)
        {
            var key = (line, name.ToUpperInvariant());
            if (!_state.Identities.TryGetValue(key, out var identity)) _state.Identities.Add(key, identity = new());
            return identity;
        }
        private void Enter(Node node)
        {
            _cancellation.ThrowIfCancellationRequested();
            if (_state.Stack.Contains(node))
                throw new RpgCompileException(node.Line, "Cyclic declaration type dependency: " + string.Join(" -> ", _state.Stack.Select(n => n.Name).Append(node.Name)) + ".");
            if (_state.Stack.Count >= 64) throw new RpgCompileException(node.Line, "Declaration type dependencies exceed 64 levels.");
            _state.Stack.Add(node);
        }
        private void Leave() => _state.Stack.RemoveAt(_state.Stack.Count - 1);
        private static string Keywords(IEnumerable<DeclarationKeyword> options) => string.Join(' ', options.Select(k => k.Name + (k.Arguments is null ? "" : "(" + k.Arguments + ")")));
        internal void Scalar(string name, string text, int line)
        { name = name.ToUpperInvariant(); _values.TryAdd(name, new(name, text, line)); }
        internal void Constant(string name, string text, int line)
        { name = name.ToUpperInvariant(); _values.TryAdd(name, new(name, text, line, "constant")); }
        internal void Signature(bool entry, string name, string text, int line)
        {
            var node = new Node("__RETURN", text, line, "return");
            if (entry) _entry = node; else _returns.TryAdd(name, node);
        }
        internal void Parameter(bool entry, string name, string text, int line)
        { if (entry) { Scalar(name, text, line); _entryNames.Add(name); } }

        private void Index()
        {
            if (_indexed) return;
            _indexed = true;
            var lines = new List<string>(_source);
            var discovery = new RpgProgram { Name = _program.Name, Library = _program.Library, NativeProcedureName = _program.NativeProcedureName };
            for (var index = 0; index < lines.Count; index++)
            {
                _cancellation.ThrowIfCancellationRequested();
                var raw = lines[index];
                if (NativeDefinitionShift(raw) is { } shift)
                { ReadNativeFixedDefinition(lines, ref index, discovery, shift, false, _cancellation, this); continue; }
                var text = (!_free && raw.Length >= 8 && raw[..7].All(c => c == ' ' || char.IsAsciiDigit(c)) ? raw[7..] : raw).Trim();
                if (Regex.IsMatch(text, @"\ADCL-(PR|PI)\b", RegexOptions.IgnoreCase))
                { ReadFreeProgramSignature(lines, ref index, discovery, false, !_free, _cancellation, this); continue; }
                if (Regex.IsMatch(text, @"\ADCL-DS\b", RegexOptions.IgnoreCase))
                {
                    var first = index; var cursor = index;
                    var header = ReadFreeDeclaration(lines, ref cursor, index + 1, !_free);
                    var options = Regex.Match(header, @"\ADCL-DS\s+\S+\s*(.*)\z", RegexOptions.IgnoreCase).Groups[1].Value;
                    if (!DeclarationKeywords(options, first + 1).Any(k => k.Name is "LIKEDS" or "LIKEREC"))
                    {
                        while (++cursor < lines.Count)
                        {
                            if (string.IsNullOrWhiteSpace(lines[cursor]) || lines[cursor].TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                            var declaration = ReadFreeDeclaration(lines, ref cursor, cursor + 1, !_free);
                            if (declaration.StartsWith("END-DS", StringComparison.OrdinalIgnoreCase)) break;
                        }
                        if (cursor >= lines.Count) throw new RpgCompileException(first + 1, "DCL-DS without END-DS.");
                    }
                    StructureImage(_source, first, cursor, !_free); index = cursor; continue;
                }
                if (!Regex.IsMatch(text, @"\ADCL-[SC]\b", RegexOptions.IgnoreCase)) continue;
                var line = index + 1; text = ReadFreeDeclaration(lines, ref index, line, !_free);
                var match = Regex.Match(text, @"\ADCL-([SC])\s+([A-Za-z_][A-Za-z0-9_]*)\s+(.+)\z", RegexOptions.IgnoreCase);
                if (!match.Success) throw new RpgCompileException(line, "Invalid declaration in type dependency index.");
                if (match.Groups[1].Value.Equals("C", StringComparison.OrdinalIgnoreCase)) Constant(match.Groups[2].Value, match.Groups[3].Value, line);
                else Scalar(match.Groups[2].Value, match.Groups[3].Value, line);
            }
        }

        internal void StructureImage(List<string> source, int first, int last, bool columnLimited = false)
        {
            var lines = new List<string>(source); var cursor = first;
            var header = ReadFreeDeclaration(lines, ref cursor, first + 1, columnLimited);
            var match = Regex.Match(header, @"\ADCL-DS\s+([A-Za-z_][A-Za-z0-9_]*|\*N)(?:\s+(.*))?\z", RegexOptions.IgnoreCase);
            if (!match.Success) throw new RpgCompileException(first + 1, "Invalid structure in type dependency index.");
            var name = match.Groups[1].Value.ToUpperInvariant(); var options = DeclarationKeywords(match.Groups[2].Value, first + 1);
            var owner = new Owner(name, Keywords(options.Where(k => k.Name != "INZ")), first + 1);
            owner.InheritInitialization = options.Any(k => k.Name == "INZ" && k.Arguments is not null);
            _owners.Add(owner.Line, owner);
            if (name != "*N") { _structures.TryAdd(name, owner); _values.TryAdd(name, owner.Root); }
            var image = Enumerable.Repeat("", last - first + 1).ToArray();
            image[0] = "DCL-DS " + name + " " + owner.Text + ";";
            var initializedImage = new string[image.Length]; Array.Fill(initializedImage, "");
            initializedImage[0] = "DCL-DS " + name + " " + Keywords(options) + ";";
            for (cursor++; cursor <= last; cursor++)
            {
                if (string.IsNullOrWhiteSpace(lines[cursor]) || lines[cursor].TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                var line = cursor + 1; var declaration = ReadFreeDeclaration(lines, ref cursor, line, columnLimited);
                if (declaration.StartsWith("END-DS", StringComparison.OrdinalIgnoreCase))
                { image[line - first - 1] = initializedImage[line - first - 1] = "END-DS;"; break; }
                var member = Regex.Match(declaration, @"\A(?:DCL-SUBF\s+)?([A-Za-z_][A-Za-z0-9_]*|\*N)\s+(.+)\z", RegexOptions.IgnoreCase);
                if (!member.Success) throw new RpgCompileException(line, "Invalid structure member in type dependency index.");
                var local = member.Groups[1].Value.ToUpperInvariant(); var text = member.Groups[2].Value;
                initializedImage[line - first - 1] = "DCL-SUBF " + local + " " + text + ";";
                if (!text.StartsWith('*')) text = Keywords(DeclarationKeywords(text, line).Where(k => k.Name != "INZ"));
                image[line - first - 1] = "DCL-SUBF " + local + " " + text + ";";
                if (local == "*N") continue;
                var node = new Node(local, text, line, text.StartsWith('*') ? "feedback" : "field", owner);
                owner.Members.TryAdd(local, node);
                if (!options.Any(k => k.Name is "QUALIFIED" or "LIKEDS" or "LIKEREC")) _values.TryAdd(local, node);
            }
            owner.Image = image; owner.InitializedImage = initializedImage;
        }

        private RpgField? Target(string name, RpgDataStructure? current)
        {
            _cancellation.ThrowIfCancellationRequested(); Index();
            var parts = name.Split('.');
            if (current is not null && _owners.TryGetValue(current.DeclarationLine, out var owner) &&
                (parts.Length == 1 || parts.Length == 2 && parts[0].Equals(owner.Name, StringComparison.OrdinalIgnoreCase)))
                if (Member(owner, parts[^1]) is { } member) return member;
            if (parts.Length == 2)
            {
                if (_structures.TryGetValue(parts[0], out var structure)) return Member(structure, parts[1]);
                if (_values.TryGetValue(parts[0], out var parameter))
                {
                    var keywords = DeclarationKeywords(parameter.Text, parameter.Line);
                    if (keywords.FirstOrDefault()?.Name == "LIKEDS") return StructureMember(keywords[0].Arguments ?? "", parts[1]);
                    if (keywords.FirstOrDefault()?.Name == "LIKEREC" && Resolve(parameter)?.ParameterStructure?.Find(parts[1]) is { } member)
                        return new() { Name = member.Name, Kind = member.Kind, Length = member.Length, Decimals = member.Decimals, LikeType = member.LikeType };
                    return null;
                }
            }
            else
            {
                if (_values.TryGetValue(name, out var value)) return value.Kind == "constant" ? null : Resolve(value);
                if (_returns.TryGetValue(name, out var returns)) return Resolve(returns);
                if (_children.TryGetValue(name, out var child)) { child.Index(); return child._entry is { } entry ? child.Resolve(entry) : null; }
            }
            return _parent?.Target(name, null);
        }
        private RpgField? Member(Owner owner, string name)
        {
            if (owner.Members.TryGetValue(name, out var node)) return Resolve(node);
            var options = DeclarationKeywords(owner.Text, owner.Line);
            if (options.SingleOrDefault(k => k.Name == "LIKEREC")?.Arguments is { } record)
            {
                var element = RecordTemplate(Preview(owner.Line), record, owner.Line).Find(name);
                return element is null ? null : new() { Name = element.Name, Kind = element.Kind, Length = element.Length,
                    Decimals = element.Decimals, LikeType = element.LikeType, IsArray = element.IsArray, Dimension = element.Dimension };
            }
            return options.SingleOrDefault(k => k.Name == "LIKEDS")?.Arguments is { } parent ? StructureMember(parent, name) : null;
        }
        private RpgField? StructureMember(string owner, string name)
        {
            Index(); return _structures.TryGetValue(owner, out var structure) ? Member(structure, name) : _parent?.StructureMember(owner, name);
        }
        private RpgDataStructure? Template(string name, bool initialization, int line)
        {
            _cancellation.ThrowIfCancellationRequested(); Index();
            if (_structures.TryGetValue(name, out var owner))
            {
                var structure = Structure(owner, initialization);
                var length = StructureLength(owner, structure);
                if (length > structure.Length || _program.DeclarationStructureLengths.GetValueOrDefault(owner.Name) == 99)
                {
                    structure = CloneWithInitialization(structure, structure.InitializeSubfields);
                    structure.Length = length;
                    if (_program.DeclarationStructureLengths.GetValueOrDefault(owner.Name) == 99) structure.IndicatorDataStructure = true;
                }
                return structure;
            }
            if (_values.TryGetValue(name, out var node))
            {
                if (node.Kind != "constant" && DeclarationKeywords(node.Text, node.Line).FirstOrDefault()?.Name is "LIKEDS" or "LIKEREC")
                    return Resolve(node)?.ParameterStructure;
                throw new RpgCompileException(line, "LIKEDS requires a named native structure: " + name + ".");
            }
            return _parent?.Template(name, initialization, line);
        }

        private RpgDataStructure Structure(string name)
        {
            Index();
            if (_structures.TryGetValue(name, out var owner)) return Structure(owner);
            if (_parent is not null) return _parent.Structure(name);
            throw new RpgCompileException(0, "Missing structure type " + name + ".");
        }
        private RpgDataStructure Structure(Owner owner, bool initialization = false)
        {
            if ((initialization ? owner.InitializedValue : owner.Value) is { } cached) return cached;
            if (_program.FindDs(owner.Name) is { } actual && actual.DeclarationLine == owner.Line)
            { if (initialization) owner.InitializedValue = actual; else owner.Value = actual; return actual; }
            if (initialization) _ = Structure(owner);

            Enter(owner.Root);
            try
            {
                var scope = Preview(owner.Line);
                var options = DeclarationKeywords(owner.Text, owner.Line);
                if (options.SingleOrDefault(k => k.Name == "LIKEDS")?.Arguments is { } parent)
                {
                    var template = Template(parent, initialization && owner.InheritInitialization, owner.Line) ?? Structure(parent);
                    scope.DataStructures.RemoveAll(d => d.Name.Equals(template.Name, StringComparison.OrdinalIgnoreCase));
                    scope.DataStructures.Add(template);
                }
                var lines = Enumerable.Repeat("", owner.Line - 1).Concat(initialization ? owner.InitializedImage : owner.Image).ToList(); var index = owner.Line - 1;
                DeclareNativeStructure(lines, ref index, scope, _cancellation);
                var result = scope.DataStructures[^1];
                if (initialization) owner.InitializedValue = result; else owner.Value = result;
                return result;
            }
            catch (RpgCompileException error) when (error.LineNumber == 0 && error.Location is null)
            { throw new RpgCompileException(owner.Line, error.Message); }
            finally { Leave(); }
        }
        private int StructureLength(string name)
        {
            if (Template(name, false, 0) is { } structure) return structure.Length;
            throw new RpgCompileException(0, "Missing structure type " + name + ".");
        }
        private int StructureLength(Owner owner, RpgDataStructure structure)
        {
            var inherited = DeclarationKeywords(owner.Text, owner.Line).SingleOrDefault(k => k.Name == "LIKEDS")?.Arguments;
            return Math.Max(structure.Length, Math.Max(_program.DeclarationStructureLengths.GetValueOrDefault(owner.Name),
                inherited is null ? 0 : StructureLength(inherited)));
        }
        private RpgField? Resolve(Node node)
        {
            _cancellation.ThrowIfCancellationRequested();
            if (node.Resolved) return node.Value;
            if (node.Kind == "structure")
            {
                var ds = Structure(_owners[node.Line]);
                return new() { Name = ds.Name, Kind = RpgFieldKind.Character, Length = StructureLength(_owners[node.Line], ds),
                    LikeType = ds.LikeType, IsDataStructure = true, ParameterStructure = ds, OpaqueBytes = true, IsArray = ds.IsArray, Dimension = ds.Dimension };
            }
            Enter(node);
            try
            {
                var scope = Preview(node.Line);
                if (node.Kind == "constant")
                {
                    scope.Fields.RemoveAll(f => f.Name.Equals(node.Name, StringComparison.OrdinalIgnoreCase));
                    DeclareFreeConstant(scope, "DCL-C " + node.Name + " " + node.Text, node.Line);
                    node.Value = scope.Fields[^1];
                }
                else if (node.Kind == "feedback")
                {
                    var feedback = FeedbackSubfield(node.Text, DeclarationKeywords(node.Owner!.Text, node.Owner.Line).Any(k => k.Name == "PSDS"), node.Line);
                    node.Value = new() { Name = node.Name, Kind = feedback.Kind, Length = feedback.Width, LikeType = Identity(node.Line, node.Name) };
                }
                else
                {
                    var options = DeclarationKeywords(node.Text, node.Line);
                    if (options.Count == 0 || options[0].Name is "EXTPGM" or "EXTPROC") { node.Resolved = true; return null; }
                    if (options[0].Name is "LIKEDS" or "LIKEREC")
                    {
                        var scratch = new RpgProgram { Name = scope.Name, Library = scope.Library };
                        DeclareNativeParameter(scratch, scope, node.Name, Keywords(options.Where(k => k.Name is "LIKEDS" or "LIKEREC" or "DIM")), node.Line, false);
                        node.Value = scratch.Fields.Single();
                    }
                    else
                    {
                        var retained = options.Take(1).Concat(options.Skip(1).Where(k => k.Name is "CCSID" or "DIM" or "ASCEND" or "DESCEND" or "CTDATA" or "PERRCD" or "EXTFMT" or "ALT" or "TEMPLATE"));
                        var scratch = new RpgProgram { Name = scope.Name, Library = scope.Library };
                        scratch.CompileTimeData.AddRange(_program.CompileTimeData);
                        var current = node.Owner is { } owner ? new RpgDataStructure { Name = owner.Name, DeclarationLine = owner.Line } : null;
                        var template = options.Any(k => k.Name == "TEMPLATE");
                        DeclareFreeScalar(scratch, node.Name, Keywords(retained), node.Line, scope, current, standaloneStorage: template, parameter: !template);
                        node.Value = scratch.Fields.Single();
                    }
                }
                node.Resolved = true; return node.Value;
            }
            catch (RpgCompileException error) when (error.LineNumber == 0 && error.Location is null)
            { throw new RpgCompileException(node.Line, error.Message); }
            finally { Leave(); }
        }
        private RpgProgram Preview(int boundary)
        {
            var scope = new RpgProgram { Name = _program.Name, Library = _program.Library, NativeProcedureName = _program.NativeProcedureName,
                GlobalProgram = _program.GlobalProgram ?? _parent?._program };
            if (_parent is not null) ImportGlobalSymbols(scope, _parent._program);
            scope.RecordLayoutsLookup = _program.RecordLayoutsLookup ?? _parent?._program.RecordLayoutsLookup;
            scope.DeclarationDiskFiles.AddRange(_program.DeclarationDiskFiles.Where(f => !scope.DeclarationDiskFiles.Contains(f)));
            scope.Fields.AddRange(_program.Fields.Where(f => !scope.Fields.Contains(f)));
            scope.DataStructures.AddRange(_program.DataStructures.Where(d => !scope.DataStructures.Contains(d)));
            scope.GlobalFields.UnionWith(_program.GlobalFields); scope.GlobalStructures.UnionWith(_program.GlobalStructures);
            foreach (var (name, length) in _program.DeclarationStructureLengths) scope.DeclarationStructureLengths[name] = length;
            scope.ForwardTypeLookup = Target; scope.ForwardStructureLookup = Template; scope.DeclarationIdentityLookup = Identity;
            scope.PreviewLayoutLookup = Target;
            scope.PreviewConstantLookup = name => ConstantBefore(name, boundary);
            if (_entry is { } entry && entry.Line < boundary)
            {
                scope.HasNativeEntryInterface = true; scope.NativeParameterLists.Add("/*ENTRY", _entryNames);
                if (DeclarationKeywords(entry.Text, entry.Line).Any(k => k.Name == "RTNPARM")) scope.NativeReturnField = new() { Name = "__RETURN", Length = 1, ReturnByParameter = true };
            }
            return scope;
        }
        private RpgField? ConstantBefore(string name, int boundary)
        {
            Index();
            if (_values.TryGetValue(name, out var node)) return node.Kind == "constant" && node.Line < boundary ? Resolve(node) : null;
            return _parent?.ConstantBefore(name, int.MaxValue);
        }
    }
}
