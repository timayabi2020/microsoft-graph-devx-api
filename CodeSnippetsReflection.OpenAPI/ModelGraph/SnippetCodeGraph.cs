﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.Specialized;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using CodeSnippetsReflection.StringExtensions;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Services;

namespace CodeSnippetsReflection.OpenAPI.ModelGraph
{
    public record SnippetCodeGraph
    {

        private static readonly Regex nestedStatementRegex = new(@"(\w+)(\([^)]+\))", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(5));

        private static readonly Regex splitCommasExcludingBracketsRegex = new(@"([^,\(\)]+(\(.*?\))*)+", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(5));

        private static readonly CodeProperty EMPTY_PROPERTY = new() { Name = null, Value = null, Children = null, PropertyType = PropertyType.Default };

        private static Dictionary<string, PropertyType> _formatPropertyTypes = new (StringComparer.OrdinalIgnoreCase)
        {
            {"int32", PropertyType.Int32},
            {"int64", PropertyType.Int64},
            {"double", PropertyType.Double},
            {"float32", PropertyType.Float32},
            {"float64", PropertyType.Float64},
            {"duration", PropertyType.Duration},
            {"boolean", PropertyType.Boolean},
            {"base64url", PropertyType.Base64Url},
            {"guid", PropertyType.Guid},
            {"uuid", PropertyType.Guid},
            {"date-time", PropertyType.DateTime},
            {"time", PropertyType.TimeOnly},
            {"date", PropertyType.DateOnly}
        };
        
        private static readonly char NamespaceNameSeparator = '.';
        
        public SnippetCodeGraph(HttpRequestMessage requestPayload, string serviceRootUrl, OpenApiUrlTreeNode treeNode) : this(new SnippetModel(requestPayload, serviceRootUrl, treeNode)) {}

        public SnippetCodeGraph(SnippetModel snippetModel)
        {
            ResponseSchema = snippetModel.ResponseSchema;
            RequestSchema = snippetModel.RequestSchema;
            HttpMethod = snippetModel.Method;
            Nodes = snippetModel.PathNodes;
            Headers = parseHeaders(snippetModel);
            Options = Enumerable.Empty<CodeProperty>();
            Parameters = parseParameters(snippetModel);
            Body = parseBody(snippetModel);
            ApiVersion = snippetModel.ApiVersion;
        }

        public OpenApiSchema ResponseSchema
        {
            get; set;
        }
        
        public OpenApiSchema RequestSchema
        {
            get; set;
        }

        public string ApiVersion
        {
            get; set;
        }

        public HttpMethod HttpMethod
        {
            get; set;
        }

        public IEnumerable<CodeProperty> Headers
        {
            get; set;
        }
        public IEnumerable<CodeProperty> Options
        {
            get; set;
        }

        public IEnumerable<CodeProperty> Parameters
        {
            get; set;
        }

        public CodeProperty Body
        {
            get; set;
        }

        public IEnumerable<OpenApiUrlTreeNode> Nodes
        {
            get; set;
        }

       
        public Boolean HasHeaders()
        {
            return Headers.Any();
        }

        public Boolean HasOptions()
        {
            return Options.Any();
        }

        public Boolean HasParameters()
        {
            return Parameters.Any();
        }

        public Boolean HasBody()
        {
            return Body.PropertyType != PropertyType.Default;
        }

        public Boolean HasReturnedBody(){
            return !(ResponseSchema == null || (ResponseSchema.Properties.Count == 1 && ResponseSchema.Properties.First().Key.Equals("error", StringComparison.OrdinalIgnoreCase)));
        }

        ///
        /// Parses Headers Filtering Out 'Host'
        ///
        private static IEnumerable<CodeProperty> parseHeaders(SnippetModel snippetModel)
        {
            return snippetModel.RequestHeaders.Where(h => !h.Key.Equals("Host", StringComparison.OrdinalIgnoreCase))
                .Select(h => new CodeProperty { Name = h.Key, Value = h.Value?.FirstOrDefault(), Children = null, PropertyType = PropertyType.String })
                .ToList();
        }

        private static List<CodeProperty> parseParameters(SnippetModel snippetModel)
        {

            var ArrayParameters = ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase,"select", "expand", "orderby");
            var NumberParameters = ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase,"skip", "top");

            var parameters = new List<CodeProperty>();
            if (!string.IsNullOrEmpty(snippetModel.QueryString))
            {
                var (queryString, replacements) = ReplaceNestedOdataQueryParameters(snippetModel.QueryString);

                NameValueCollection queryCollection = HttpUtility.ParseQueryString(queryString);
                foreach (String key in queryCollection.AllKeys)
                {
                    var name = NormalizeQueryParameterName(key).Trim();
                    var value = GetQueryParameterValue(queryCollection[key], replacements);
                    if(ArrayParameters.Contains(name)){
                        var children = splitCommasExcludingBracketsRegex.Split(value)
                            .Where(x => !String.IsNullOrEmpty(x) && !x.StartsWith("(") && !x.Equals(","))
                            .Select(x => new CodeProperty() { Name = null, Value = x, PropertyType = PropertyType.String }).ToList();

                        parameters.Add(new() { Name = name.ToLower(), Value = null, PropertyType = PropertyType.Array , Children = children});
                    }else if (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("false", StringComparison.OrdinalIgnoreCase)){
                        parameters.Add(new() { Name = name.ToLower(), Value = value, PropertyType = PropertyType.Boolean });
                    }else if (NumberParameters.Contains(name)){
                        parameters.Add(new() { Name = name.ToLower(), Value = value, PropertyType = PropertyType.Int32 });
                    }else{
                        parameters.Add(new() { Name = name, Value = value, PropertyType = PropertyType.String });
                    }
                }
            }
            return parameters;
        }

        private static string NormalizeQueryParameterName(string queryParam) => HttpUtility.UrlDecode(queryParam.TrimStart('$').ToFirstCharacterLowerCase());

        private static (string, Dictionary<string, string>) ReplaceNestedOdataQueryParameters(string queryParams)
        {
            var replacements = new Dictionary<string, string>();
            var matches = nestedStatementRegex.Matches(queryParams);
            if (matches.Any())
                foreach (var groups in matches.Select(m => m.Groups))
                {
                    var key = groups[1].Value;
                    var value = groups[2].Value;
                    replacements.Add(key, value);
                    queryParams = queryParams.Replace(value, string.Empty);
                }
            return (queryParams, replacements);
        }

        private static string GetQueryParameterValue(string originalValue, Dictionary<string, string> replacements)
        {
            var escapedParam = System.Web.HttpUtility.UrlDecode(originalValue);
            if (escapedParam.Equals("true", StringComparison.OrdinalIgnoreCase) || escapedParam.Equals("false", StringComparison.OrdinalIgnoreCase))
                return escapedParam.ToLowerInvariant();
            else if (int.TryParse(escapedParam, out var intValue))
                return intValue.ToString();
            else
            {
                return escapedParam.Split(',')
                    .Select(v => replacements.ContainsKey(v) ? v + replacements[v] : v)
                    .Aggregate((a, b) => $"{a},{b}");
            }
        }

        private static CodeProperty parseBody(SnippetModel snippetModel)
        {
            if (string.IsNullOrWhiteSpace(snippetModel?.RequestBody))
                return EMPTY_PROPERTY;

            switch (snippetModel.ContentType?.Split(';').First().ToLowerInvariant())
            {
                case "application/json":
                    return TryParseBody(snippetModel);
                case "application/octet-stream":
                    return new() { Name = null, Value = snippetModel.RequestBody, Children = null, PropertyType = PropertyType.Binary };
                default:
                    return TryParseBody(snippetModel);//in case the content type header is missing but we still have a json payload
            }
        }

        private static string ComputeRequestBody(SnippetModel snippetModel)
        {
            var requestBodySuffix = $"{snippetModel.Method.Method.ToLower().ToFirstCharacterUpperCase()}RequestBody"; // calculate the suffix using the HttpMethod
            var name = snippetModel.RequestSchema?.Reference?.GetClassName();

            if(!string.IsNullOrEmpty(name))
                return name?.ToFirstCharacterUpperCase();

            var nodes = snippetModel.PathNodes;
            if (!(nodes?.Any() ?? false)) return string.Empty;
                
            var nodeName = nodes.Where(x => !x.Segment.IsCollectionIndex())
                .Select(x =>
                {
                    if (x.Segment.IsFunction())
                        return x.Segment.Split('.').Last();
                    else
                        return x.Segment;
                })
                .Last()
                .ToFirstCharacterUpperCase();

            var singularNodeName = nodeName[nodeName.Length - 1] == 's' ? nodeName.Substring(0, nodeName.Length - 1) : nodeName;

            if (nodes.Last()?.Segment?.IsCollectionIndex() == true)
                return singularNodeName;
            else
                return nodeName?.Append(requestBodySuffix);
        }

        private static CodeProperty TryParseBody(SnippetModel snippetModel)
        {
            if (!snippetModel.IsRequestBodyValid)
                throw new InvalidOperationException($"Unsupported content type: {snippetModel.ContentType}");

            using var parsedBody = JsonDocument.Parse(snippetModel.RequestBody, new JsonDocumentOptions { AllowTrailingCommas = true });
            var schema = snippetModel.RequestSchema;
            var className = schema.GetSchemaTitle().ToFirstCharacterUpperCase() ?? ComputeRequestBody(snippetModel);
            return parseJsonObjectValue(className, parsedBody.RootElement, schema, snippetModel.EndPathNode);
        }

        private static CodeProperty parseJsonObjectValue(String rootPropertyName, JsonElement value, OpenApiSchema schema, OpenApiUrlTreeNode currentNode = null)
        {
            var children = new List<CodeProperty>();

            if (value.ValueKind != JsonValueKind.Object) throw new InvalidOperationException($"Expected JSON object and got {value.ValueKind}");

            var propertiesAndSchema = value.EnumerateObject()
                                            .Select(x => new Tuple<JsonProperty, OpenApiSchema>(x, schema.GetPropertySchema(x.Name)));
            foreach (var propertyAndSchema in propertiesAndSchema.Where(x => x.Item2 != null))
            {
                var propertyName = propertyAndSchema.Item1.Name.ToFirstCharacterLowerCase();
                children.Add(parseProperty(propertyName, propertyAndSchema.Item1.Value, propertyAndSchema.Item2));
            }

            var propertiesWithoutSchema = propertiesAndSchema.Where(x => x.Item2 == null).Select(x => x.Item1);
            if (propertiesWithoutSchema.Any())
            {

                var additionalChildren = new List<CodeProperty>();
                foreach (var property in propertiesWithoutSchema)
                    additionalChildren.Add(parseProperty(property.Name, property.Value, null));

                if (additionalChildren.Any())
                    children.Add(new CodeProperty { Name = "additionalData", PropertyType = PropertyType.Map, Children = additionalChildren });
            }

            return new CodeProperty
            {
                Name = rootPropertyName, 
                PropertyType = PropertyType.Object, 
                Children = children, 
                TypeDefinition = schema.GetSchemaTitle()?.ToFirstCharacterUpperCase() ?? GetComposedSchema(schema).GetSchemaTitle()?.ToFirstCharacterUpperCase() ?? rootPropertyName, 
                NamespaceName = GetNamespaceFromSchema(schema) ??  currentNode.GetNodeNamespaceFromPath(string.Empty)
            };
        }

        private static OpenApiSchema GetComposedSchema(OpenApiSchema schema)
        {
            if (schema == null)
                return null;
            
            var typesCount = schema.AnyOf?.Count ?? schema.OneOf?.Count ?? 0;
            if ((typesCount == 1 && schema.Nullable &&
                 schema.IsAnyOf()) || // nullable on the root schema outside of anyOf
                typesCount == 2 && schema.IsAnyOf() && schema.AnyOf.Any(static x => // nullable on a schema in the anyOf
                    x.Nullable &&
                    !x.Properties.Any() &&
                    !x.IsOneOf() &&
                    !x.IsAnyOf() &&
                    !x.IsAllOf() &&
                    !x.IsArray() &&
                    !x.IsReferencedSchema())) // once openAPI 3.1 is supported, there will be a third case oneOf with Ref and type null.
            {
                return schema.AnyOf.FirstOrDefault(static x => !string.IsNullOrEmpty(x.GetSchemaTitle()));
            }

            return null;
        }

        private static string GetNamespaceFromSchema(OpenApiSchema schema)
        {
            if (schema == null)
                return null;

            return GetModelsNamespaceNameFromReferenceId(schema.IsReferencedSchema() ? schema.Reference?.Id : GetComposedSchema(schema)?.Reference?.Id);
        }

        private static String escapeSpecialCharacters(string value)
        {
            return value?.Replace("\"", "\\\"")?.Replace("\n", "\\n")?.Replace("\r", "\\r");
        }
        
        private static string GetModelsNamespaceNameFromReferenceId(string referenceId) {
            if (string.IsNullOrEmpty(referenceId)) 
                return referenceId;
            
            referenceId = referenceId.Trim(NamespaceNameSeparator);
            var lastDotIndex = referenceId.LastIndexOf(NamespaceNameSeparator);
            var namespaceSuffix = lastDotIndex != -1 ? $".{referenceId[..lastDotIndex]}" : string.Empty;
            return $"models{namespaceSuffix}";
        }
        
        private static CodeProperty evaluateStringProperty(string propertyName, JsonElement value, OpenApiSchema propSchema)
        {
            if ((propSchema?.Type?.Equals("boolean", StringComparison.OrdinalIgnoreCase) ?? false))
                return new CodeProperty { Name = propertyName, Value = value.GetString(), PropertyType = PropertyType.Boolean, Children = new List<CodeProperty>() };
            var formatString = propSchema?.Format;
            if (!string.IsNullOrEmpty(formatString) && _formatPropertyTypes.TryGetValue(formatString, out var type))
                return new CodeProperty { Name = propertyName, Value = value.GetString(), PropertyType = type, Children = new List<CodeProperty>() };
            var enumSchema = propSchema?.AnyOf.FirstOrDefault(x => x.Enum.Count > 0);
            if ((propSchema?.Enum.Count ?? 0) == 0 && enumSchema == null)
                return new CodeProperty { Name = propertyName, Value = escapeSpecialCharacters(value.GetString()), PropertyType = PropertyType.String, Children = new List<CodeProperty>() };
            enumSchema ??= propSchema;
            var propValue = String.IsNullOrWhiteSpace(value.GetString()) ? null : $"{enumSchema?.Title.ToFirstCharacterUpperCase()}.{value.GetString().ToFirstCharacterUpperCase()}";
            // Pass the list of options in the enum as children so that the language generators may use them for validation if need be, 
            var enumValueOptions = enumSchema?.Enum.Where(option => option is OpenApiString)
                                                                .Select(option => new CodeProperty{Name = ((OpenApiString)option).Value,Value = ((OpenApiString)option).Value,PropertyType = PropertyType.String})
                                                                .ToList() ?? new List<CodeProperty>();
            return new CodeProperty { Name = propertyName, Value = propValue, PropertyType = PropertyType.Enum, Children = enumValueOptions ,NamespaceName = GetNamespaceFromSchema(enumSchema)};
        }

        private static CodeProperty evaluateNumericProperty(string propertyName, JsonElement value, OpenApiSchema propSchema)
        {
             if(propSchema == null) 
                return new CodeProperty { Name = propertyName, Value = $"{value}", PropertyType = PropertyType.Int32, Children = new List<CodeProperty>() };

             var schemas = (propSchema.AnyOf ?? Enumerable.Empty<OpenApiSchema>())
                 .Union(propSchema.AllOf ?? Enumerable.Empty<OpenApiSchema>())
                 .Union(propSchema.OneOf ?? Enumerable.Empty<OpenApiSchema>())
                 .ToList();
             
             schemas.Add(propSchema);

             var types = schemas.Select(item => item.Type).Where(static x => !string.IsNullOrEmpty(x)).ToList();
             var formats = schemas.Select(item => item.Format).Where(static x => !string.IsNullOrEmpty(x)).ToList();
                
            var (propertyType, propertyValue) = types switch
            {
                _ when types.Contains("integer") && formats.Contains("int32") => (PropertyType.Int32 , value.GetInt32().ToString()),
                _ when types.Contains("integer") && formats.Contains("int64") => (PropertyType.Int64 , value.GetInt64().ToString()),
                _ when formats.Contains("float")  || formats.Contains("float32")  => (PropertyType.Float32, value.GetDecimal().ToString()),
                _ when formats.Contains("float64") => (PropertyType.Float64, value.GetDecimal().ToString()),
                _ when formats.Contains("double") => (PropertyType.Double, value.GetDouble().ToString()), //in MS Graph float & double are any of number, string and enum
                _ => (PropertyType.Int32, $"{value.GetInt32()}"),
            };

            return new CodeProperty { Name = propertyName, Value = propertyValue, PropertyType = propertyType, Children = new List<CodeProperty>() };
        }

        private static CodeProperty parseProperty(string propertyName, JsonElement value, OpenApiSchema propSchema)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    return evaluateStringProperty(propertyName, value, propSchema);
                case JsonValueKind.Number:
                    return evaluateNumericProperty(propertyName, value, propSchema);
                case JsonValueKind.False:
                case JsonValueKind.True:
                    return new CodeProperty { Name = propertyName, Value = value.GetBoolean().ToString(), PropertyType = PropertyType.Boolean, Children = new List<CodeProperty>() };
                case JsonValueKind.Null:
                    return new CodeProperty { Name = propertyName, Value = "null", PropertyType = PropertyType.Null, Children = new List<CodeProperty>() };
                case JsonValueKind.Object:
                    if (propSchema != null)
                        return parseJsonObjectValue(propertyName, value, propSchema);
                    else
                        return parseAnonymousObjectValues(propertyName, value, propSchema);
                case JsonValueKind.Array:
                    return parseJsonArrayValue(propertyName, value, propSchema);
                default:
                    throw new NotImplementedException($"Unsupported JsonValueKind: {value.ValueKind}");
            }
        }

        private static CodeProperty parseJsonArrayValue(string propertyName, JsonElement value, OpenApiSchema schema)
        {
            var alternativeType = schema?.Items?.AnyOf?.FirstOrDefault()?.AllOf?.LastOrDefault()?.Title;
            var children = value.EnumerateArray().Select(item => parseProperty(schema.GetSchemaTitle() ?? alternativeType?.ToFirstCharacterUpperCase(), item, schema?.Items)).ToList();
            var genericType = schema.GetSchemaTitle().ToFirstCharacterUpperCase() ??
                              (value.EnumerateArray().Any() ?
                                  value.EnumerateArray().First().ValueKind.ToString() :
                                  schema?.Items?.Type);
            return new CodeProperty { Name = propertyName, Value = null, PropertyType = PropertyType.Array, Children = children, TypeDefinition = genericType ?? alternativeType };
        }

        private static CodeProperty parseAnonymousObjectValues(string propertyName, JsonElement value, OpenApiSchema schema)
        {
            if (value.ValueKind != JsonValueKind.Object) throw new InvalidOperationException($"Expected JSON object and got {value.ValueKind}");

            var children = new List<CodeProperty>();
            var propertiesAndSchema = value.EnumerateObject()
                                            .Select(x => new Tuple<JsonProperty, OpenApiSchema>(x, schema.GetPropertySchema(x.Name)));
            foreach (var propertyAndSchema in propertiesAndSchema)
            {
                children.Add(parseProperty(propertyAndSchema.Item1.Name.ToFirstCharacterLowerCase(), propertyAndSchema.Item1.Value, propertyAndSchema.Item2));
            }

            return new CodeProperty { Name = propertyName, Value = null, PropertyType = PropertyType.Object, Children = children };
        }
    }

}
