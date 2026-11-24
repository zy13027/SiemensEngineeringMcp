using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Siemens.Engineering;
using Siemens.Engineering.HmiUnified;
using Siemens.Engineering.HmiUnified.HmiConnections;
using Siemens.Engineering.HmiUnified.HmiTags;
using Siemens.Engineering.HmiUnified.UI.Dynamization;
using Siemens.Engineering.HmiUnified.UI.ScreenGroup;
using Siemens.Engineering.HmiUnified.UI.Screens;
using Siemens.Engineering.HmiUnified.UI.Widgets;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;

namespace SiemensEngineeringMcp;

internal sealed partial class EngineeringSession
{
    private readonly Dictionary<string, HmiChange> _hmiPreviews = new();

    private sealed class HmiChange
    {
        public string DevicePath = "";
        public string ScreenPath = "";
        public string ItemName = "";
        public string Property = "";
        public string TagName = "";
        public string PlcTag = "";
        public string ConnectionName = "";
        public string TablePath = "";
        public string Before = "";
    }

    internal object DispatchUnified(string operation, JsonElement arguments, JsonElement profile)
    {
        var devicePath = RequiredString(profile, "hmiPath");
        var device = FindDevice(devicePath);
        if (operation == "hmi_compile")
        {
            RequireEngineeringWrite();
            MarkEngineeringChanged();
            return Compile(device);
        }
        var software = FindHmiSoftware(device.DeviceItems)
            ?? throw new InvalidOperationException($"No WinCC Unified software was found below '{devicePath}'.");
        return operation switch
        {
            "hmi_discover" => DiscoverHmi(software, devicePath),
            "hmi_read" => ReadHmi(software, devicePath, arguments),
            "hmi_preview" => PreviewHmi(software, devicePath, arguments),
            "hmi_apply" => ApplyHmi(software, devicePath, RequiredString(arguments, "previewId")),
            _ => throw new InvalidOperationException($"Unknown Unified engineering operation '{operation}'.")
        };
    }

    private static HmiSoftware? FindHmiSoftware(DeviceItemComposition items)
    {
        foreach (DeviceItem item in items)
        {
            if (item.GetService<SoftwareContainer>()?.Software is HmiSoftware software)
                return software;
            var nested = FindHmiSoftware(item.DeviceItems);
            if (nested != null)
                return nested;
        }
        return null;
    }

    private static object DiscoverHmi(HmiSoftware software, string devicePath) => new
    {
        devicePath,
        connections = software.Connections.Select(HmiConnectionState).ToArray(),
        screens = HmiScreens(software).Select(screen => new
        {
            path = screen.Path,
            items = screen.Screen.ScreenItems.Select(item => new
            {
                name = item.Name,
                type = item.GetType().Name,
                editableProperty = item is HmiIOField ? "ProcessValue" : null
            }).ToArray()
        }).ToArray(),
        tagTables = HmiTables(software).Select(table => new
        {
            path = table.Path,
            tags = table.Table.Tags.Select(HmiTagState).ToArray()
        }).ToArray()
    };

    private static object ReadHmi(HmiSoftware software, string devicePath, JsonElement arguments)
    {
        var change = HmiTarget(devicePath, arguments);
        var item = ResolveHmiField(software, change);
        var match = FindHmiTag(software, change.TagName);
        return new
        {
            devicePath,
            change.ScreenPath,
            change.ItemName,
            change.Property,
            binding = HmiBindingState(item.Dynamizations.Find(change.Property)),
            tagTablePath = match.TablePath,
            tag = HmiTagState(match.Tag)
        };
    }

    private object PreviewHmi(HmiSoftware software, string devicePath, JsonElement arguments)
    {
        var change = HmiTarget(devicePath, arguments);
        change.PlcTag = RequiredString(arguments, "plcTag");
        change.ConnectionName = RequiredString(arguments, "connectionName");
        var requestedTable = arguments.TryGetProperty("tagTablePath", out var tablePath) &&
            tablePath.ValueKind == JsonValueKind.String ? tablePath.GetString() : null;
        var match = FindHmiTag(software, change.TagName);
        if (match.Tag != null)
        {
            if (requestedTable != null && !string.Equals(match.TablePath, requestedTable, StringComparison.Ordinal))
                throw new InvalidOperationException($"Tag '{change.TagName}' already belongs to '{match.TablePath}'.");
            change.TablePath = match.TablePath!;
        }
        else
        {
            var tables = HmiTables(software).ToArray();
            if (requestedTable == null && tables.Length != 1)
                throw new InvalidOperationException("Specify tagTablePath from hmi_discover when the HMI does not have exactly one tag table.");
            change.TablePath = requestedTable ?? tables[0].Path;
            if (tables.Count(table => string.Equals(table.Path, change.TablePath, StringComparison.Ordinal)) != 1)
                throw new InvalidOperationException($"Tag table '{change.TablePath}' was not found uniquely.");
        }

        var before = ReadHmiForChange(software, change);
        change.Before = JsonSerializer.Serialize(before);
        var id = Guid.NewGuid().ToString("N");
        if (_hmiPreviews.Count >= 128)
            _hmiPreviews.Remove(_hmiPreviews.Keys.First());
        _hmiPreviews.Add(id, change);

        return new
        {
            previewId = id,
            devicePath,
            change.ScreenPath,
            change.ItemName,
            change.Property,
            tagTablePath = change.TablePath,
            before,
            proposed = new { tag = change.TagName, plcTag = change.PlcTag, connection = change.ConnectionName },
            changesApplied = false
        };
    }

    private object ApplyHmi(HmiSoftware software, string devicePath, string previewId)
    {
        RequireEngineeringWrite();
        if (!_hmiPreviews.TryGetValue(previewId, out var change) || change.DevicePath != devicePath)
            throw new InvalidOperationException("The Unified preview is unknown or targets a different device.");

        var expectedPlcName = GetPlc().Name;
        using var access = Portal.ExclusiveAccess();
        var before = ReadHmiForChange(software, change);
        if (!string.Equals(change.Before, JsonSerializer.Serialize(before), StringComparison.Ordinal))
            throw new InvalidOperationException($"Unified preview '{previewId}' is stale. Preview the change again.");

        object after;
        using (var transaction = access.Transaction(Project, "Update MCP Unified tag and screen binding"))
        {
            var field = ResolveHmiField(software, change);
            var tag = FindHmiTag(software, change.TagName).Tag;
            if (tag == null)
            {
                var table = HmiTables(software).Single(entry => entry.Path == change.TablePath).Table;
                tag = software.Tags.Create(change.TagName, table.Name);
                var createdTag = FindHmiTag(software, change.TagName);
                if (!string.Equals(createdTag.TablePath, change.TablePath, StringComparison.Ordinal))
                    throw new InvalidOperationException($"The created tag was not found in '{change.TablePath}'.");
            }
            tag.Connection = change.ConnectionName;
            tag.PlcTag = change.PlcTag;

            var binding = field.Dynamizations.Find(change.Property) as TagDynamization
                ?? field.Dynamizations.Create<TagDynamization>(change.Property);
            binding.Tag = tag.Name;
            if (!string.Equals(binding.Tag, change.TagName, StringComparison.Ordinal) ||
                !string.Equals(tag.Connection, change.ConnectionName, StringComparison.Ordinal) ||
                !string.Equals(tag.PlcTag, change.PlcTag, StringComparison.Ordinal) ||
                !string.Equals(tag.PlcName, expectedPlcName, StringComparison.Ordinal))
                throw new InvalidOperationException($"The Unified tag or screen binding failed readback for PLC '{expectedPlcName}'.");

            after = ReadHmiForChange(software, change);
            transaction.CommitOnDispose();
        }
        MarkEngineeringChanged();
        _hmiPreviews.Remove(previewId);
        return new { previewId, devicePath, change.ScreenPath, change.ItemName, change.Property, after, changesApplied = true, saved = false };
    }

    private static HmiChange HmiTarget(string devicePath, JsonElement arguments) => new()
    {
        DevicePath = devicePath,
        ScreenPath = RequiredString(arguments, "screenPath"),
        ItemName = RequiredString(arguments, "itemName"),
        Property = RequiredString(arguments, "property"),
        TagName = RequiredString(arguments, "tagName")
    };

    private static object ReadHmiForChange(HmiSoftware software, HmiChange change)
    {
        var field = ResolveHmiField(software, change);
        var binding = field.Dynamizations.Find(change.Property);
        if (binding != null && binding is not TagDynamization)
            throw new InvalidOperationException($"'{change.Property}' already has a {binding.GetType().Name} dynamization.");
        var match = FindHmiTag(software, change.TagName);
        if (match.Tag != null && match.TablePath != change.TablePath)
            throw new InvalidOperationException($"Tag '{change.TagName}' moved to '{match.TablePath}'.");
        var matchingTable = HmiTables(software).ToArray();
        if (matchingTable.Count(table => table.Path == change.TablePath) != 1)
            throw new InvalidOperationException($"Tag table '{change.TablePath}' was not found uniquely.");
        if (match.Tag == null && matchingTable.Count(table => table.Table.Name ==
            matchingTable.Single(target => target.Path == change.TablePath).Table.Name) != 1)
            throw new InvalidOperationException($"Tag table '{change.TablePath}' has an ambiguous name for tag creation.");

        var connection = software.Connections.Find(change.ConnectionName)
            ?? throw new InvalidOperationException($"HMI connection '{change.ConnectionName}' was not found.");
        return new
        {
            itemType = field.GetType().Name,
            binding = HmiBindingState(binding),
            tagTablePath = match.TablePath,
            tag = HmiTagState(match.Tag),
            connection = HmiConnectionState(connection)
        };
    }

    private static HmiIOField ResolveHmiField(HmiSoftware software, HmiChange change)
    {
        var matches = HmiScreens(software).Where(screen => screen.Path == change.ScreenPath).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException($"Screen '{change.ScreenPath}' was not found uniquely.");
        var item = matches[0].Screen.ScreenItems.Find(change.ItemName)
            ?? throw new InvalidOperationException($"Item '{change.ItemName}' was not found on '{change.ScreenPath}'.");
        if (item is not HmiIOField field || change.Property != "ProcessValue")
            throw new InvalidOperationException("This release supports the ProcessValue property of an HMI I/O field.");
        return field;
    }

    private static object HmiConnectionState(HmiConnection connection) => new
    {
        name = connection.Name,
        station = connection.Station,
        partner = connection.Partner,
        communicationDriver = connection.CommunicationDriver.ToString()
    };

    private static object? HmiTagState(HmiTag? tag) => tag == null ? null : new
    {
        name = tag.Name,
        plcTag = tag.PlcTag,
        connection = tag.Connection,
        plcName = tag.PlcName,
        tagType = tag.TagType.ToString()
    };

    private static object? HmiBindingState(DynamizationBase? binding) => binding == null ? null : new
    {
        type = binding.GetType().Name,
        property = binding.PropertyName,
        tag = (binding as TagDynamization)?.Tag
    };

    private static (string? TablePath, HmiTag? Tag) FindHmiTag(HmiSoftware software, string name)
    {
        var matches = HmiTables(software)
            .Select(table => (table.Path, Tag: table.Table.Tags.Find(name)))
            .Where(match => match.Tag != null)
            .ToArray();
        if (matches.Length > 1)
            throw new InvalidOperationException($"HMI tag '{name}' is ambiguous across tag tables.");
        return matches.Length == 0 ? (null, null) : matches[0];
    }

    private static IEnumerable<(string Path, HmiScreen Screen)> HmiScreens(HmiSoftware software)
    {
        foreach (var screen in software.Screens)
            yield return (screen.Name, screen);
        foreach (var group in software.ScreenGroups)
            foreach (var screen in HmiScreens(group, group.Name))
                yield return screen;
    }

    private static IEnumerable<(string Path, HmiScreen Screen)> HmiScreens(HmiScreenGroup group, string path)
    {
        foreach (var screen in group.Screens)
            yield return ($"{path}/{screen.Name}", screen);
        foreach (var child in group.Groups)
            foreach (var screen in HmiScreens(child, $"{path}/{child.Name}"))
                yield return screen;
    }

    private static IEnumerable<(string Path, HmiTagTable Table)> HmiTables(HmiSoftware software)
    {
        foreach (var table in software.TagTables)
            yield return (table.Name, table);
        foreach (var group in software.TagTableGroups)
            foreach (var table in HmiTables(group, group.Name))
                yield return table;
    }

    private static IEnumerable<(string Path, HmiTagTable Table)> HmiTables(HmiTagTableGroup group, string path)
    {
        foreach (var table in group.TagTables)
            yield return ($"{path}/{table.Name}", table);
        foreach (var child in group.Groups)
            foreach (var table in HmiTables(child, $"{path}/{child.Name}"))
                yield return table;
    }
}

