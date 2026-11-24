using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;

namespace SiemensEngineeringMcp
{
    internal sealed partial class EngineeringSession
    {
        private const int MaxSourceLength = 262144;
        private const int PreviewLimit = 32;
        private static readonly Regex BodyStart = new Regex(
            @"^[ \t]*(?:NETWORK|BEGIN)\b", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        private static readonly Regex BlockEnd = new Regex(
            @"\bEND_(?:FUNCTION_BLOCK|FUNCTION|ORGANIZATION_BLOCK|DATA_BLOCK|TYPE)\b", RegexOptions.IgnoreCase);
        private readonly Dictionary<string, PlcChange> _plcPreviews = new Dictionary<string, PlcChange>();

        private PlcSoftware GetPlc()
        {
            var device = FindDevice(RequiredString(_profile, "plcPath"));
            var targets = AllItems(device.DeviceItems)
                .Select(item => item.GetService<SoftwareContainer>()?.Software)
                .OfType<PlcSoftware>()
                .ToArray();
            if (targets.Length != 1)
                throw new InvalidOperationException($"PLC device '{device.Name}' must expose exactly one PLC software target; found {targets.Length}.");

            return targets[0];
        }

        private static IEnumerable<DeviceItem> AllItems(IEnumerable<DeviceItem> items)
        {
            foreach (var item in items)
            {
                yield return item;
                foreach (var child in AllItems(item.DeviceItems))
                    yield return child;
            }
        }

        private object ListBlocks()
        {
            var plc = GetPlc();
            return new { plcPath = RequiredString(_profile, "plcPath"), blocks = WalkBlocks(plc.BlockGroup, "").ToArray() };
        }

        private static IEnumerable<object> WalkBlocks(PlcBlockGroup group, string prefix)
        {
            foreach (var block in group.Blocks)
            {
                yield return new
                {
                    path = prefix + block.Name,
                    name = block.Name,
                    number = block.Number,
                    language = block.ProgrammingLanguage.ToString(),
                    isConsistent = block.IsConsistent
                };
            }
            foreach (var subGroup in group.Groups)
            {
                foreach (var block in WalkBlocks(subGroup, prefix + subGroup.Name + "/"))
                    yield return block;
            }
        }

        private (PlcBlockGroup Group, PlcBlock Block) GetBlock(string path)
        {
            var parts = path.Split('/');
            if (parts.Any(part => String.IsNullOrWhiteSpace(part) || part == "." || part == ".."))
                throw new InvalidOperationException("Use an exact, root-relative block path such as 'Group/Block'.");

            PlcBlockGroup group = GetPlc().BlockGroup;
            for (var position = 0; position < parts.Length - 1; position++)
            {
                group = group.Groups.Find(parts[position])
                    ?? throw new InvalidOperationException($"PLC block group '{parts[position]}' in '{path}' does not exist.");
            }

            var block = group.Blocks.Find(parts[parts.Length - 1])
                ?? throw new InvalidOperationException($"PLC block '{path}' does not exist.");
            return (group, block);
        }

        private object PlcRead(string path)
        {
            var (_, block) = GetBlock(path);
            using (var scratch = ScratchDirectory.Create(RequiredString(_profile, "fileRoot")))
            {
                var source = ExportScl(block, scratch.Directory);
                var body = BodyStart.Match(source.Text);
                return new
                {
                    blockPath = path,
                    format = "simatic-s7dcl",
                    language = block.ProgrammingLanguage.ToString(),
                    source = source.Text,
                    declaration = body.Success ? source.Text.Substring(0, body.Index) : null,
                    contentHash = source.Hash
                };
            }
        }

        private object PlcPreview(string path, string after)
        {
            if (after.Length > MaxSourceLength)
                throw new InvalidOperationException($"A proposed source document cannot exceed {MaxSourceLength} characters.");

            var (_, block) = GetBlock(path);
            using (var scratch = ScratchDirectory.Create(RequiredString(_profile, "fileRoot")))
            {
                var before = ExportScl(block, scratch.Directory);
                ValidateReplacement(before.Text, after);
                if (_plcPreviews.Count >= PreviewLimit)
                    _plcPreviews.Remove(_plcPreviews.Keys.First());

                var id = Guid.NewGuid().ToString("N");
                _plcPreviews.Add(id, new PlcChange(path, before.Hash, after));
                return new
                {
                    previewId = id,
                    projectPath = _projectPath,
                    plcPath = RequiredString(_profile, "plcPath"),
                    blockPath = path,
                    format = "simatic-s7dcl",
                    beforeHash = before.Hash,
                    diff = Diff(before.Text, after)
                };
            }
        }

        private object PlcApply(string id)
        {
            RequireEngineeringWrite();
            if (!_plcPreviews.TryGetValue(id, out var change))
                throw new InvalidOperationException($"Unknown or expired PLC preview '{id}'. Create a new preview.");

            using (var exclusive = Portal.ExclusiveAccess("Apply one reviewed SCL body"))
            using (var scratch = ScratchDirectory.Create(RequiredString(_profile, "fileRoot")))
            {
                var (group, block) = GetBlock(change.Path);
                var before = ExportScl(block, scratch.Directory);
                if (before.Hash != change.BeforeHash)
                    throw new InvalidOperationException($"PLC block '{change.Path}' changed since preview. Preview it again.");

                ValidateReplacement(before.Text, change.After);
                _plcPreviews.Remove(id);
                File.WriteAllText(before.Path, change.After, before.Encoding);

                var name = block.Name;
                var number = block.Number;
                using (var transaction = exclusive.Transaction(Project, "Replace SCL body"))
                {
                    var result = group.Blocks.ImportFromDocuments(scratch.Directory, Path.GetFileNameWithoutExtension(before.Path), ImportDocumentOptions.Override);
                    if (result.State != DocumentResultState.Success)
                        throw new InvalidOperationException($"TIA import for '{change.Path}' returned {result.State}: {String.Join(" | ", result.Messages.Select(message => message.Message))}");

                    var imports = result.ImportedPlcBlocks.ToArray();
                    if (imports.Length != 1 || imports[0].Name != name || imports[0].Number != number)
                        throw new InvalidOperationException($"TIA did not report exactly the intended block '{change.Path}' with its original number. The transaction was not committed.");

                    transaction.CommitOnDispose();
                }

                MarkEngineeringChanged();
                return new { blockPath = change.Path, previewId = id, applied = true, saved = false, readBack = "Compile, save, reopen and call plc_read to verify the persisted body." };
            }
        }

        private static void ValidateReplacement(string before, string after)
        {
            var original = Normalise(before);
            var proposed = Normalise(after);
            var originalStart = BodyStart.Match(original);
            var proposedStart = BodyStart.Match(proposed);
            var originalEnd = BlockEnd.Matches(original).Cast<Match>().ToArray();
            var proposedEnd = BlockEnd.Matches(proposed).Cast<Match>().ToArray();
            if (!originalStart.Success || !proposedStart.Success || originalEnd.Length != 1 || proposedEnd.Length != 1 ||
                originalEnd[0].Index <= originalStart.Index || proposedEnd[0].Index <= proposedStart.Index ||
                !String.Equals(original.Substring(0, originalStart.Index), proposed.Substring(0, proposedStart.Index), StringComparison.Ordinal) ||
                !String.Equals(original.Substring(originalEnd[0].Index), proposed.Substring(proposedEnd[0].Index), StringComparison.Ordinal) ||
                !String.IsNullOrWhiteSpace(proposed.Substring(proposedEnd[0].Index + proposedEnd[0].Length)))
                throw new InvalidOperationException("Supply one matching SCL source document. Keep the declaration and final block terminator unchanged and edit only the body.");

            if (original == proposed)
                throw new InvalidOperationException("The proposed SCL body matches the current document.");
        }

        private static SclDocument ExportScl(PlcBlock block, DirectoryInfo directory)
        {
            if (block.ProgrammingLanguage != ProgrammingLanguage.SCL || block.IsKnowHowProtected || !block.IsConsistent)
                throw new InvalidOperationException($"Block '{block.Name}' must be SCL, consistent and free of know-how protection for source export.");

            var result = block.ExportAsDocuments(directory, "Block");
            if (result.State != DocumentResultState.Success)
                throw new InvalidOperationException($"TIA export for '{block.Name}' returned {result.State}: {String.Join(" | ", result.Messages.Select(message => message.Message))}");

            var declarationFiles = directory.GetFiles("*.s7dcl");
            if (declarationFiles.Length != 1)
                throw new InvalidOperationException($"TIA did not export exactly one source document for '{block.Name}'.");

            var source = declarationFiles[0];
            if (source.Length > MaxSourceLength * 4)
                throw new InvalidOperationException($"Source document exceeds the supported size for '{block.Name}'.");

            using (var reader = new StreamReader(source.FullName, new UTF8Encoding(false, true), true))
            {
                var text = reader.ReadToEnd();
                if (text.Length > MaxSourceLength)
                    throw new InvalidOperationException($"Source document exceeds the supported size for '{block.Name}'.");

                return new SclDocument(source.FullName, text, reader.CurrentEncoding, Fingerprint(directory, block.Number));
            }
        }

        private static string Fingerprint(DirectoryInfo directory, int blockNumber)
        {
            using (var buffer = new MemoryStream())
            using (var writer = new BinaryWriter(buffer))
            using (var hash = SHA256.Create())
            {
                writer.Write(blockNumber);
                foreach (var file in directory.GetFiles().OrderBy(file => file.Name, StringComparer.Ordinal))
                {
                    writer.Write(file.Name);
                    using (var data = file.OpenRead())
                        writer.Write(hash.ComputeHash(data));
                }
                return BitConverter.ToString(hash.ComputeHash(buffer.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }

        private static string Normalise(string source) => source.Replace("\r\n", "\n").Replace('\r', '\n');

        private static string Diff(string before, string after)
        {
            var original = Normalise(before).Split('\n');
            var proposed = Normalise(after).Split('\n');
            var start = 0;
            while (start < original.Length && start < proposed.Length && original[start] == proposed[start])
                start++;

            var originalEnd = original.Length;
            var proposedEnd = proposed.Length;
            while (originalEnd > start && proposedEnd > start && original[originalEnd - 1] == proposed[proposedEnd - 1])
            {
                originalEnd--;
                proposedEnd--;
            }

            var oldCount = originalEnd - start;
            var newCount = proposedEnd - start;
            return String.Join("\n", new[] { $"@@ -{(oldCount == 0 ? start : start + 1)},{oldCount} +{(newCount == 0 ? start : start + 1)},{newCount} @@" }
                .Concat(original.Skip(start).Take(oldCount).Select(line => "-" + line))
                .Concat(proposed.Skip(start).Take(newCount).Select(line => "+" + line)));
        }

        private sealed class PlcChange
        {
            public PlcChange(string path, string beforeHash, string after)
            {
                Path = path;
                BeforeHash = beforeHash;
                After = after;
            }
            public string Path { get; }
            public string BeforeHash { get; }
            public string After { get; }
        }

        private sealed class SclDocument
        {
            public SclDocument(string path, string text, Encoding encoding, string hash)
            {
                Path = path;
                Text = text;
                Encoding = encoding;
                Hash = hash;
            }
            public string Path { get; }
            public string Text { get; }
            public Encoding Encoding { get; }
            public string Hash { get; }
        }

        private sealed class ScratchDirectory : IDisposable
        {
            private ScratchDirectory(DirectoryInfo directory) => Directory = directory;
            public DirectoryInfo Directory { get; }

            public static ScratchDirectory Create(string rootPath)
            {
                var root = new DirectoryInfo(Program.RequireAbsolutePath(rootPath, "fileRoot"));
                for (DirectoryInfo? path = root; path != null; path = path.Parent)
                {
                    if (!path.Exists || (path.Attributes & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidOperationException($"The engineering file root must exist and must not contain redirects: '{root.FullName}'.");
                }

                return new ScratchDirectory(root.CreateSubdirectory("engineering-" + Guid.NewGuid().ToString("N")));
            }

            public void Dispose()
            {
                foreach (var file in Directory.GetFiles())
                    file.Delete();
                Directory.Delete();
            }
        }
    }
}
