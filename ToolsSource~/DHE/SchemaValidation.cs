using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HybridCLR.DheTool;

internal static partial class Program
{
    private static readonly HashSet<string> SupportedSchemaKeywords = new(StringComparer.Ordinal)
    {
        "$schema", "$id", "$ref", "$defs", "$comment", "title", "description", "default",
        "examples", "deprecated", "readOnly", "writeOnly", "type", "required", "properties",
        "additionalProperties", "const", "enum", "pattern", "items", "minItems", "maxItems",
        "uniqueItems", "minLength", "maxLength", "minimum", "maximum", "allOf", "anyOf",
        "if", "then", "else", "format"
    };

    private static int SchemaValidate(Cli cli)
    {
        var schemaPath = RequireFile(cli.Require("schema"), "JSON schema");
        var documentPath = RequireFile(cli.Require("document"), "JSON document");
        var schema = ReadJson<JsonElement>(schemaPath);
        var document = ReadJson<JsonElement>(documentPath);
        var errors = new List<string>();
        ValidateSchemaVocabulary(schema, "$", errors);
        if (errors.Count == 0) ValidateJsonSchema(schema, document, schema, "$", errors);
        var output = cli.Optional("output");
        if (!string.IsNullOrWhiteSpace(output))
        {
            var reportPath = SafeReportPath(output, new[] { schemaPath, documentPath });
            WriteJson(reportPath, new
            {
                schemaVersion = 1,
                format = "hybridclr.dhe-schema-validation.json",
                generatedAtUtc = DateTimeOffset.UtcNow,
                passed = errors.Count == 0,
                schema = schemaPath,
                document = documentPath,
                errors,
                warnings = Array.Empty<string>()
            });
        }
        if (errors.Count > 0)
        {
            Console.Error.WriteLine(string.Join(Environment.NewLine, errors));
            return 1;
        }
        Console.WriteLine("DHE schema validation passed: " + documentPath);
        return 0;
    }

    private static int SchemaGate(Cli cli)
    {
        var schemasRoot = RequireDirectory(cli.Require("schemasroot"), "Schemas root");
        var inputRoot = RequireDirectory(cli.Require("inputroot"), "Schema gate input root");
        var output = SafeReportPath(cli.Require("output"), new[] { schemasRoot, inputRoot });
        var errors = new List<string>();
        var schemaRecords = LoadSchemaRegistry(schemasRoot, errors);
        var exactSchemas = schemaRecords.Where(record => record.Format != null)
            .GroupBy(record => record.Format!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var patternSchemas = schemaRecords.Where(record => record.FormatPattern != null).ToArray();
        var documents = new List<SchemaDocumentRecord>();
        var skippedDocumentCount = 0;
        foreach (var documentPath in Directory.GetFiles(inputRoot, "*.json", SearchOption.AllDirectories)
                     .Select(Path.GetFullPath).Where(path => !path.Equals(output, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(inputRoot, documentPath).Replace(Path.DirectorySeparatorChar, '/');
            try
            {
                var document = ReadJson<JsonElement>(documentPath);
                var format = GetString(document, "format");
                var candidates = new List<SchemaRegistryRecord>();
                if (format != null && exactSchemas.TryGetValue(format, out var exact)) candidates.AddRange(exact);
                if (format != null && candidates.Count == 0)
                    candidates.AddRange(patternSchemas.Where(record => Regex.IsMatch(format, record.FormatPattern!, RegexOptions.CultureInvariant)));
                if (candidates.Count == 0 && Path.GetFileName(documentPath).Equals("dhe-native-manifest.json", StringComparison.OrdinalIgnoreCase))
                    candidates.AddRange(schemaRecords.Where(record => record.Name.Equals("dhe-native-manifest.schema.json", StringComparison.OrdinalIgnoreCase)));
                if (candidates.Count == 0)
                {
                    skippedDocumentCount++;
                    var unknown = cli.Has("requireknownformats") && format != null &&
                                  format.StartsWith("hybridclr.dhe-", StringComparison.Ordinal);
                    var documentErrors = unknown ? new[] { "No registered schema for format: " + format } : Array.Empty<string>();
                    documents.Add(new SchemaDocumentRecord(relative, format, null, unknown ? "error" : "skipped", !unknown, documentErrors));
                    errors.AddRange(documentErrors.Select(error => relative + ": " + error));
                    continue;
                }
                if (candidates.Count != 1)
                {
                    var documentErrors = new[] { "Format resolves to multiple schemas: " + format };
                    documents.Add(new SchemaDocumentRecord(relative, format, null, "error", false, documentErrors));
                    errors.Add(relative + ": " + documentErrors[0]);
                    continue;
                }
                var selected = candidates[0];
                var validationErrors = new List<string>();
                ValidateJsonSchema(selected.Schema, document, selected.Schema, "$", validationErrors);
                documents.Add(new SchemaDocumentRecord(relative, format, selected.Name, "validated",
                    validationErrors.Count == 0, validationErrors.ToArray()));
                errors.AddRange(validationErrors.Select(error => relative + ": " + error));
            }
            catch (Exception ex)
            {
                documents.Add(new SchemaDocumentRecord(relative, null, null, "error", false, new[] { ex.Message }));
                errors.Add(relative + ": " + ex.Message);
            }
        }

        var schemaEvidence = schemaRecords.Select(record => new
        {
            path = record.Name,
            id = record.Id,
            format = record.Format,
            formatPattern = record.FormatPattern,
            supported = record.Errors.Length == 0,
            errors = record.Errors
        }).ToArray();
        var report = new
        {
            schemaVersion = 1,
            format = "hybridclr.dhe-schema-gate.json",
            generatedAtUtc = DateTimeOffset.UtcNow,
            passed = errors.Count == 0,
            schemaCount = schemaRecords.Count,
            documentCount = documents.Count(record => record.Status == "validated"),
            skippedDocumentCount,
            inputRoots = new[] { inputRoot },
            schemaRoots = new[] { schemasRoot },
            schemas = schemaEvidence,
            documents,
            errors,
            warnings = Array.Empty<string>()
        };
        WriteJson(output, report);

        var gateSchema = schemaRecords.SingleOrDefault(record =>
            record.Name.Equals("dhe-schema-gate.schema.json", StringComparison.OrdinalIgnoreCase));
        if (gateSchema == null)
        {
            errors.Add("The schema gate report schema is missing.");
        }
        else
        {
            var reportErrors = new List<string>();
            ValidateJsonSchema(gateSchema.Schema, ReadJson<JsonElement>(output), gateSchema.Schema, "$", reportErrors);
            errors.AddRange(reportErrors.Select(error => "schema gate report: " + error));
        }
        if (errors.Count > 0)
        {
            if (!errors.SequenceEqual(report.errors))
            {
                WriteJson(output, new
                {
                    report.schemaVersion, report.format, report.generatedAtUtc, passed = false, report.schemaCount,
                    report.documentCount, report.skippedDocumentCount, report.inputRoots, report.schemaRoots,
                    report.schemas, report.documents, errors, report.warnings
                });
            }
            Console.Error.WriteLine(string.Join(Environment.NewLine, errors));
            return 1;
        }
        Console.WriteLine("DHE schema gate passed: " + output);
        return 0;
    }

    private static List<SchemaRegistryRecord> LoadSchemaRegistry(string schemasRoot, List<string> errors)
    {
        var records = new List<SchemaRegistryRecord>();
        foreach (var path in Directory.GetFiles(schemasRoot, "*.schema.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            try
            {
                var schema = ReadJson<JsonElement>(path);
                var schemaErrors = new List<string>();
                ValidateSchemaVocabulary(schema, "$", schemaErrors);
                var format = TryGetFormatConstraint(schema, "const");
                var formatPattern = TryGetFormatConstraint(schema, "pattern");
                var record = new SchemaRegistryRecord(Path.GetFileName(path), GetString(schema, "$id"), format,
                    formatPattern, schema, schemaErrors.ToArray());
                records.Add(record);
                errors.AddRange(schemaErrors.Select(error => record.Name + ": " + error));
            }
            catch (Exception ex)
            {
                errors.Add(Path.GetFileName(path) + ": " + ex.Message);
            }
        }
        foreach (var duplicate in records.Where(record => record.Format != null)
                     .GroupBy(record => record.Format!, StringComparer.Ordinal).Where(group => group.Count() > 1))
            errors.Add("Duplicate exact format schema registration: " + duplicate.Key);
        return records;
    }

    private static string? TryGetFormatConstraint(JsonElement schema, string constraint)
    {
        if (!schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object ||
            !properties.TryGetProperty("format", out var formatSchema) || formatSchema.ValueKind != JsonValueKind.Object ||
            !formatSchema.TryGetProperty(constraint, out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        return value.GetString();
    }

    private static void ValidateSchemaVocabulary(JsonElement schema, string path, List<string> errors)
    {
        if (schema.ValueKind is JsonValueKind.True or JsonValueKind.False) return;
        if (schema.ValueKind != JsonValueKind.Object)
        {
            errors.Add(path + " is not a schema object or boolean.");
            return;
        }
        foreach (var property in schema.EnumerateObject())
            if (!SupportedSchemaKeywords.Contains(property.Name))
                errors.Add(path + " uses unsupported schema keyword '" + property.Name + "'.");
        foreach (var mapName in new[] { "$defs", "properties" })
            if (schema.TryGetProperty(mapName, out var map) && map.ValueKind == JsonValueKind.Object)
                foreach (var property in map.EnumerateObject())
                    ValidateSchemaVocabulary(property.Value, path + "." + mapName + "." + property.Name, errors);
        foreach (var childName in new[] { "items", "additionalProperties", "if", "then", "else" })
            if (schema.TryGetProperty(childName, out var child) && child.ValueKind is JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False)
                ValidateSchemaVocabulary(child, path + "." + childName, errors);
        foreach (var arrayName in new[] { "allOf", "anyOf" })
            if (schema.TryGetProperty(arrayName, out var array) && array.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var child in array.EnumerateArray())
                    ValidateSchemaVocabulary(child, path + "." + arrayName + "[" + index++ + "]", errors);
            }
    }

    private static void ValidateJsonSchema(JsonElement schema, JsonElement instance, JsonElement rootSchema,
        string path, List<string> errors)
    {
        if (schema.ValueKind == JsonValueKind.True) return;
        if (schema.ValueKind == JsonValueKind.False)
        {
            errors.Add(path + " is rejected by the schema.");
            return;
        }
        if (schema.ValueKind != JsonValueKind.Object)
        {
            errors.Add(path + " has an invalid schema node.");
            return;
        }
        if (schema.TryGetProperty("$ref", out var reference))
        {
            var value = reference.GetString() ?? "";
            if (!TryResolveLocalReference(rootSchema, value, out var target))
                errors.Add(path + " uses an unsupported or missing schema reference: " + value);
            else
                ValidateJsonSchema(target, instance, rootSchema, path, errors);
        }
        if (schema.TryGetProperty("allOf", out var allOf))
            foreach (var child in allOf.EnumerateArray()) ValidateJsonSchema(child, instance, rootSchema, path, errors);
        if (schema.TryGetProperty("anyOf", out var anyOf))
        {
            var accepted = anyOf.EnumerateArray().Any(child =>
            {
                var candidateErrors = new List<string>();
                ValidateJsonSchema(child, instance, rootSchema, path, candidateErrors);
                return candidateErrors.Count == 0;
            });
            if (!accepted) errors.Add(path + " does not match any allowed schema.");
        }
        if (schema.TryGetProperty("if", out var condition))
        {
            var conditionErrors = new List<string>();
            ValidateJsonSchema(condition, instance, rootSchema, path, conditionErrors);
            if (conditionErrors.Count == 0 && schema.TryGetProperty("then", out var thenSchema))
                ValidateJsonSchema(thenSchema, instance, rootSchema, path, errors);
            else if (conditionErrors.Count > 0 && schema.TryGetProperty("else", out var elseSchema))
                ValidateJsonSchema(elseSchema, instance, rootSchema, path, errors);
        }
        if (schema.TryGetProperty("type", out var type) && !MatchesSchemaType(type, instance))
        {
            errors.Add(path + " has the wrong JSON type.");
            return;
        }
        if (schema.TryGetProperty("const", out var constant) && !JsonEquivalent(constant, instance))
            errors.Add(path + " does not match the required constant.");
        if (schema.TryGetProperty("enum", out var allowed) &&
            !allowed.EnumerateArray().Any(item => JsonEquivalent(item, instance)))
            errors.Add(path + " is not an allowed enum value.");
        if (instance.ValueKind == JsonValueKind.Object)
        {
            var propertySchemas = schema.TryGetProperty("properties", out var properties) &&
                                  properties.ValueKind == JsonValueKind.Object ? properties : default;
            if (schema.TryGetProperty("required", out var required))
                foreach (var name in required.EnumerateArray().Select(item => item.GetString() ?? ""))
                    if (!instance.TryGetProperty(name, out _)) errors.Add(path + " is missing required property '" + name + "'.");
            foreach (var property in instance.EnumerateObject())
            {
                if (propertySchemas.ValueKind == JsonValueKind.Object &&
                    propertySchemas.TryGetProperty(property.Name, out var propertySchema))
                {
                    ValidateJsonSchema(propertySchema, property.Value, rootSchema, path + "." + property.Name, errors);
                }
                else if (schema.TryGetProperty("additionalProperties", out var additional))
                {
                    if (additional.ValueKind == JsonValueKind.False)
                        errors.Add(path + " has unsupported property '" + property.Name + "'.");
                    else if (additional.ValueKind is JsonValueKind.Object or JsonValueKind.True)
                        ValidateJsonSchema(additional, property.Value, rootSchema, path + "." + property.Name, errors);
                }
            }
        }
        if (instance.ValueKind == JsonValueKind.Array)
        {
            var length = instance.GetArrayLength();
            if (schema.TryGetProperty("minItems", out var minItems) && length < minItems.GetInt32())
                errors.Add(path + " has too few items.");
            if (schema.TryGetProperty("maxItems", out var maxItems) && length > maxItems.GetInt32())
                errors.Add(path + " has too many items.");
            if (schema.TryGetProperty("uniqueItems", out var unique) && unique.ValueKind == JsonValueKind.True)
            {
                var values = instance.EnumerateArray().ToArray();
                for (var left = 0; left < values.Length; left++)
                    for (var right = left + 1; right < values.Length; right++)
                        if (JsonEquivalent(values[left], values[right]))
                        {
                            errors.Add(path + " contains duplicate items.");
                            left = values.Length;
                            break;
                        }
            }
            if (schema.TryGetProperty("items", out var itemSchema))
            {
                var index = 0;
                foreach (var item in instance.EnumerateArray())
                    ValidateJsonSchema(itemSchema, item, rootSchema, path + "[" + index++ + "]", errors);
            }
        }
        if (instance.ValueKind == JsonValueKind.String)
        {
            var value = instance.GetString() ?? "";
            if (schema.TryGetProperty("minLength", out var minLength) && value.Length < minLength.GetInt32())
                errors.Add(path + " is too short.");
            if (schema.TryGetProperty("maxLength", out var maxLength) && value.Length > maxLength.GetInt32())
                errors.Add(path + " is too long.");
            if (schema.TryGetProperty("pattern", out var pattern) &&
                !Regex.IsMatch(value, pattern.GetString() ?? "", RegexOptions.CultureInvariant))
                errors.Add(path + " does not match its pattern.");
            if (schema.TryGetProperty("format", out var format) && format.GetString() == "date-time" &&
                !IsRfc3339DateTime(value)) errors.Add(path + " is not an RFC 3339 date-time.");
        }
        if (instance.ValueKind == JsonValueKind.Number)
        {
            var value = instance.GetDecimal();
            if (schema.TryGetProperty("minimum", out var minimum) && value < minimum.GetDecimal())
                errors.Add(path + " is below its minimum.");
            if (schema.TryGetProperty("maximum", out var maximum) && value > maximum.GetDecimal())
                errors.Add(path + " is above its maximum.");
        }
    }

    private static bool TryResolveLocalReference(JsonElement root, string reference, out JsonElement result)
    {
        result = root;
        if (reference == "#") return true;
        if (!reference.StartsWith("#/", StringComparison.Ordinal)) return false;
        foreach (var rawSegment in reference[2..].Split('/'))
        {
            var segment = rawSegment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty(segment, out result)) return false;
        }
        return true;
    }

    private static bool MatchesSchemaType(JsonElement type, JsonElement value)
    {
        if (type.ValueKind == JsonValueKind.Array)
            return type.EnumerateArray().Any(item => MatchesSchemaType(item, value));
        return type.GetString() switch
        {
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) &&
                         decimal.Truncate(number) == number,
            "number" => value.ValueKind == JsonValueKind.Number,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "null" => value.ValueKind == JsonValueKind.Null,
            _ => false
        };
    }

    private static bool JsonEquivalent(JsonElement left, JsonElement right)
    {
        if (left.ValueKind == JsonValueKind.Number && right.ValueKind == JsonValueKind.Number)
            return left.TryGetDecimal(out var leftNumber) && right.TryGetDecimal(out var rightNumber)
                ? leftNumber == rightNumber
                : left.GetDouble().Equals(right.GetDouble());
        if (left.ValueKind != right.ValueKind) return false;
        if (left.ValueKind == JsonValueKind.Object)
        {
            var leftProperties = left.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
            var rightProperties = right.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
            return leftProperties.Count == rightProperties.Count && leftProperties.All(pair =>
                rightProperties.TryGetValue(pair.Key, out var value) && JsonEquivalent(pair.Value, value));
        }
        if (left.ValueKind == JsonValueKind.Array)
        {
            var leftValues = left.EnumerateArray().ToArray();
            var rightValues = right.EnumerateArray().ToArray();
            return leftValues.Length == rightValues.Length &&
                   leftValues.Zip(rightValues).All(pair => JsonEquivalent(pair.First, pair.Second));
        }
        return left.ValueKind switch
        {
            JsonValueKind.String => left.GetString() == right.GetString(),
            JsonValueKind.True or JsonValueKind.False => left.GetBoolean() == right.GetBoolean(),
            JsonValueKind.Null => true,
            _ => left.GetRawText() == right.GetRawText()
        };
    }

    private static bool IsRfc3339DateTime(string value) =>
        Regex.IsMatch(value, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant) &&
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _);

    private sealed record SchemaRegistryRecord(string Name, string? Id, string? Format, string? FormatPattern,
        JsonElement Schema, string[] Errors);

    private sealed record SchemaDocumentRecord(string Path, string? Format, string? Schema, string Status,
        bool Passed, string[] Errors);
}
