using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DocExtractor
{
    public class MarkdownOutput
    {
        public string Path { get; set; }
        public string Content { get; set; }
        public string Title { get; set; }
    }

    public static class MarkdownRenderer
    {
        public static string GenerateMarkdownTableOfContentsForDocXML(IEnumerable<DocumentedSymbol> documentedSymbols, Configuration configuration)
        {
            var pathPrefix = configuration.PathPrefix;
            var indentLevel = configuration.SummaryIndentLevel;

            var symbolDict = RendererUtility.CreateSymbolDictionary(documentedSymbols);

            var namespaces = documentedSymbols.Where(d => d.Syntax is NamespaceDeclarationSyntax);

            var stringBuilder = new System.Text.StringBuilder();

            if (configuration.OutputFrontMatter)
            {
                var slug = string.Join("/", configuration.SlugPrefix, "summary");
                stringBuilder.AppendLine("---");
                stringBuilder.AppendLine($"title: Summary");
                stringBuilder.AppendLine($"slug: {slug}");
                stringBuilder.AppendLine("---");
            }

            string LineItem(int level, DocumentedSymbol symbol)
            {
                var indent = new string(' ', indentLevel);
                var levelIndent = new string(' ', level * 2);
                var label = $"{symbol.DisplayName}{(symbol.Syntax is NamespaceDeclarationSyntax ? " Namespace" : string.Empty)}";
                var link = GetMarkdownLink(label, symbol, configuration);
                return $"{indent}{levelIndent}* {link}";
            }

            var linkStack = new Stack<(DocumentedSymbol Symbol, int Level)>();

            foreach (var @namespace in namespaces)
            {
                linkStack.Push((@namespace, 0));

                while (linkStack.Count > 0)
                {
                    var (Symbol, Level) = linkStack.Pop();

                    stringBuilder.AppendLine(LineItem(Level, Symbol));

                    var children = documentedSymbols
                        .Where(d => d.Syntax is not NamespaceDeclarationSyntax &&
                        d.ContainerID == Symbol.DocumentationID &&
                        Program.IsValidSyntaxForLinking(d.Syntax, configuration, true))
                        .OrderByDescending(d => d.DocumentationID);
;
                    foreach (var child in children)
                    {
                        linkStack.Push((child, Level + 1));
                    }
                }
            }

            return stringBuilder.ToString();
        }

        public static string EscapeMarkdownCharacters(string label)
        {
            var replacedCharacters = new string[] {
                    "\\", "<", ">", "(", ")", "#", "`", "[", "]",
                };

            foreach (var character in replacedCharacters)
            {
                label = label.Replace(character, "\\" + character);
            }

            return label;
        }

        public static string GetMarkdownLink(DocumentedSymbol symbol, Configuration configuration, string localLink = null, bool asCode = false, bool useFullDisplayName = false)
        {
            var displayName = useFullDisplayName ? symbol.FullDisplayName : symbol.DisplayName;

            return GetMarkdownLink(displayName, symbol, configuration, localLink, asCode);
        }

        public static string GetMarkdownLink(string label, DocumentedSymbol symbol, Configuration configuration, string localLink = null, bool asCode = false)
        {
            if (symbol.AnchorName == null || !Program.IsValidSyntaxForLinking(symbol.Syntax, configuration, false))
            {
                return EscapeMarkdownCharacters(label);
            }
            else
            {
                label = EscapeMarkdownCharacters(label);

                var linkText = asCode ? $"`{label}`" : label;

                var ext = configuration.StripExtensionFromLinks ? string.Empty : ".md";

                if (localLink != null)
                {
                    if(localLink.StartsWith("./"))
                    {
                        localLink = $"{localLink}{ext}";
                    }

                    return $"[{linkText}]({localLink})";
                }

                return $"[{linkText}]({configuration.PathPrefix}/{symbol.AnchorName}{ext})";
            }
        }

        internal static string GetSymbolTypeLink(string typeName, DefaultDictionary<string, DocumentedSymbol> symbolDict, Configuration configuration)
        {
            string link = null;

            foreach (var pair in symbolDict)
            {
                if (pair.Key.Substring(2) == typeName)
                {
                    link = $"./{typeName.ToLowerInvariant()}";
                }
            }

            if (link == null)
            {
                return null;
            }

            if (link != null)
            {
                var index = link.IndexOf('<');

                if (index >= 0)
                {
                    link = link.Substring(0, index);
                }

                index = link.IndexOf('(');

                if (index >= 0)
                {
                    link = link.Substring(0, index);
                }
            }

            var ext = configuration.StripExtensionFromLinks ? string.Empty : ".md";

            return $"{link}{ext}";
        }

        internal static string GetTypeLinkString(string typeName, DefaultDictionary<string, DocumentedSymbol> symbolDict, Configuration configuration)
        {
            var localLink = GetSymbolTypeLink(typeName, symbolDict, configuration);

            var text = EscapeMarkdownCharacters(Program.NormalizeTypeName(typeName));

            return localLink != null ? $"[{text}]({localLink})" : text;
        }

        internal static string GetTypeLinkString(ISymbol symbol, DefaultDictionary<string, DocumentedSymbol> symbolDict, Configuration configuration)
        {
            return GetTypeLinkString(Program.GetSymbolType(symbol), symbolDict, configuration);
        }

        internal static string FormatMethodName(ISymbol self, ISymbol parent, DefaultDictionary<string, DocumentedSymbol> symbolDict,
            Configuration configuration)
        {
            if (self is not IMethodSymbol methodSymbol)
            {
                return self.Name;
            }

            var outValue = self.Name;

            if (outValue == ".ctor")
            {
                outValue = parent.Name;
            }

            if (methodSymbol.TypeArguments.Length > 0)
            {
                outValue += $"\\<{string.Join(", ", methodSymbol.TypeArguments.Select(x =>
                {
                    return $"{GetTypeLinkString(x.ToDisplayString(NullableFlowState.None), symbolDict, configuration)}";
                }))}\\>";
            }

            outValue += $"({string.Join(", ", methodSymbol.Parameters.Select(x =>
            {
                return $"{GetTypeLinkString(x.Type.ToDisplayString(NullableFlowState.None), symbolDict, configuration)} {x.Name}";
            }))})";

            return outValue;
        }

        public static List<MarkdownOutput> GenerateMarkdownForDocXML(
            IEnumerable<DocumentedSymbol> documentedSymbols,
            Configuration configuration)
        {
            var symbolDict = RendererUtility.CreateSymbolDictionary(documentedSymbols);

            var outputs = new List<MarkdownOutput>();

            // var types = documentedSymbols.Where(s => s.DocumentationID.StartsWith("T:"));

            foreach (var member in documentedSymbols)
            {
                var symbol = symbolDict[member.DocumentationID];

                if (!Program.IsValidSyntaxForLinking(symbol.Syntax, configuration, true) ||
                    member.Symbol.DeclaredAccessibility == Accessibility.Private)
                {
                    continue;
                }

                var path = symbol.AnchorName + ".md";
                var title = string.Empty;
                var xml = XElement.Parse(symbol.DocumentationXml);

                if (symbol.Syntax is NamespaceDeclarationSyntax)
                {
                    title = symbol.FullDisplayName + " Namespace";
                }
                else if(symbol.Syntax is MethodDeclarationSyntax)
                {
                    title = FormatMethodName(symbol.Symbol, symbol.Symbol.ContainingSymbol, symbolDict, configuration);
                }
                else
                {
                    title =  symbol.FullDisplayName;
                }

                var stringBuilder = new System.Text.StringBuilder();

                void HandleChild(XElement childDocs)
                {
                    var childSummary = CreateMarkdownFromXMLTags(symbolDict, childDocs.Element("summary")).Replace("\n", " ").Trim();

                    stringBuilder.AppendLine(EscapeMarkdown(childSummary));

                    // Add remarks, if present
                    if (childDocs.Element("remarks") != null)
                    {
                        stringBuilder.AppendLine($"**Remarks**: {CreateMarkdownFromXMLTags(symbolDict, childDocs.Element("remarks"))}");
                        stringBuilder.AppendLine();
                    }

                    var paramNodes = childDocs.Elements("param");

                    if (paramNodes.Any())
                    {
                        stringBuilder.AppendLine("#### Parameters");
                        stringBuilder.AppendLine();

                        foreach (var param in paramNodes)
                        {
                            var parameterTypeID = param.Attribute("typeID")?.Value;
                            var parameterTypeName = param.Attribute("typeName")?.Value;

                            DocumentedSymbol parameterTypeSymbol = null;

                            if (parameterTypeID != null)
                            {
                                symbolDict.TryGetValue(parameterTypeID, out parameterTypeSymbol);
                            }

                            if (parameterTypeSymbol != null)
                            {
                                stringBuilder.Append(GetMarkdownLink(Program.NormalizeTypeName(parameterTypeName), parameterTypeSymbol, configuration));
                            }
                            else if (parameterTypeName != null)
                            {
                                stringBuilder.Append(EscapeMarkdownCharacters(Program.NormalizeTypeName(parameterTypeName)));
                            }
                            else
                            {
                                // We don't have a type symbol or even a type name
                                // for this parameter! Can't write a parameter type.
                            }

                            stringBuilder.Append(' ');

                            stringBuilder.Append(param.Attribute("name").Value);

                            var summary = CreateMarkdownFromXMLTags(symbolDict, param).Replace("\n", " ").Trim();

                            stringBuilder.AppendLine(summary.Length > 0 ? $" - {summary}" : "");

                            stringBuilder.AppendLine();
                        }

                        stringBuilder.AppendLine();
                    }

                    var typeParamNodes = childDocs.Elements("typeparam");

                    if (typeParamNodes.Any())
                    {
                        stringBuilder.AppendLine("#### Type Parameters");
                        stringBuilder.AppendLine();

                        foreach (var param in typeParamNodes)
                        {
                            stringBuilder.Append($"{param.Attribute("name").Value} {CreateMarkdownFromXMLTags(symbolDict, param).Replace("\n", " ").Trim()}");
                        }

                        stringBuilder.AppendLine();
                    }

                    // Generate a 'Returns' section only if we actually have content
                    // for it
                    var returns = childDocs.Element("returns");

                    if (returns != null && returns.Nodes().Any())
                    {
                        stringBuilder.AppendLine("#### Returns");
                        stringBuilder.AppendLine();
                        stringBuilder.AppendLine(CreateMarkdownFromXMLTags(symbolDict, childDocs.Element("returns")));
                        stringBuilder.AppendLine();
                    }

                    var exceptions = childDocs.Elements("except");

                    if (exceptions.Any())
                    {
                        stringBuilder.AppendLine("#### Exceptions");
                        stringBuilder.AppendLine();

                        foreach (var exception in exceptions)
                        {
                            var cref = exception.Attribute("cref")?.Value;
                            var exceptionSymbol = symbolDict[cref];

                            stringBuilder.AppendLine($"{GetMarkdownLink(exceptionSymbol, configuration)} {CreateMarkdownFromXMLTags(symbolDict, exception).Replace("\n", " ").Trim()}");
                        }

                        stringBuilder.AppendLine();
                    }

                    stringBuilder.AppendLine();
                }

                if (configuration.OutputFrontMatter)
                {
                    var slug = string.Join("/", configuration.SlugPrefix, symbol.AnchorName);
                    stringBuilder.AppendLine("---");
                    stringBuilder.AppendLine($"title: {title}");
                    stringBuilder.AppendLine($"slug: {slug}");
                    stringBuilder.AppendLine("---");
                }
                else
                {
                    stringBuilder.AppendLine("# " + title);
                }

                HTMLRenderer.GetTypeName(symbol, false, out var typeName);

                if (configuration.OutputMemberFiles && symbol.Syntax is not NamespaceDeclarationSyntax)
                {
                    var parent = documentedSymbols.FirstOrDefault(s => symbol.ContainerID == s.DocumentationID);

                    if (parent != null)
                    {
                        stringBuilder.AppendLine($"{typeName} in {GetMarkdownLink(parent, configuration)}");
                        stringBuilder.AppendLine();
                    }
                }

                if (symbol.BaseTypeID != null)
                {
                    var symbolName = "";

                    if (symbolDict.TryGetValue(symbol.BaseTypeID, out var baseSymbol))
                    {
                        symbolName = GetMarkdownLink(baseSymbol, configuration, asCode: true);
                    }
                    else
                    {
                        symbolName = symbol.BaseTypeID.Substring(2);
                    }

                    if(!symbolName.StartsWith("System."))
                    {
                        stringBuilder.AppendLine($"Inherits from {symbolName}");
                    }
                }

                var isObsolete = symbol.Symbol.GetAttributes().Any(a => a.AttributeClass.GetDocumentationCommentId() == "T:System.ObsoleteAttribute");

                if (isObsolete)
                {
                    stringBuilder.AppendLine(CreateCallout("warning", $"This {typeName?.ToLowerInvariant() ?? "item"} is <b>obsolete</b> and may be removed from a future version of Yarn Spinner."));
                    stringBuilder.AppendLine();
                }

                var summary = xml.Element("summary");

                if (summary != null)
                {
                    string value = CreateMarkdownFromXMLTags(symbolDict, summary);

                    stringBuilder.AppendLine(value);
                    stringBuilder.AppendLine();
                }

                // Show the declaration, if this is not a namespace
                if (symbol.Syntax is not NamespaceDeclarationSyntax)
                {
                    stringBuilder.AppendLine("```csharp");
                    stringBuilder.AppendLine(symbol.Declaration);
                    stringBuilder.AppendLine("```");
                }

                stringBuilder.AppendLine();

                // Add remarks, if present
                if (xml.Element("remarks") != null)
                {
                    stringBuilder.AppendLine($"**Remarks**: {CreateMarkdownFromXMLTags(symbolDict, xml.Element("remarks"))}");

                    stringBuilder.AppendLine();
                }

                var paramNodes = xml.Elements("param");

                if (paramNodes.Any())
                {
                    stringBuilder.AppendLine("## Parameters");
                    stringBuilder.AppendLine();

                    foreach (var param in paramNodes)
                    {
                        var parameterTypeID = param.Attribute("typeID")?.Value;
                        var parameterTypeName = param.Attribute("typeName")?.Value;

                        DocumentedSymbol parameterTypeSymbol = null;

                        if (parameterTypeID != null)
                        {
                            symbolDict.TryGetValue(parameterTypeID, out parameterTypeSymbol);
                        }

                        if (parameterTypeSymbol != null)
                        {
                            stringBuilder.Append(GetMarkdownLink(Program.NormalizeTypeName(parameterTypeName), parameterTypeSymbol, configuration));
                        }
                        else if (parameterTypeName != null)
                        {
                            stringBuilder.Append($"`{Program.NormalizeTypeName(parameterTypeName)}`");
                        }
                        else
                        {
                            // We don't have a type symbol or even a type name
                            // for this parameter! Can't write a parameter type.
                        }

                        stringBuilder.Append(' ');

                        stringBuilder.AppendLine(param.Attribute("name").Value);

                        stringBuilder.AppendLine(CreateMarkdownFromXMLTags(symbolDict, param).Replace("\n", " ").Trim());
                    }

                    stringBuilder.AppendLine();
                }

                var typeParamNodes = xml.Elements("typeparam");

                if (typeParamNodes.Any())
                {
                    stringBuilder.AppendLine("## Type Parameters");
                    stringBuilder.AppendLine();

                    foreach (var param in typeParamNodes)
                    {
                        stringBuilder.Append($"{param.Attribute("name").Value} {CreateMarkdownFromXMLTags(symbolDict, param).Replace("\n", " ").Trim()}");
                    }

                    stringBuilder.AppendLine();
                }

                // Generate a 'Returns' section only if we actually have content
                // for it
                XElement returns = xml.Element("returns");

                if (returns != null && returns.Nodes().Any())
                {
                    stringBuilder.AppendLine("## Returns");
                    stringBuilder.AppendLine();
                    stringBuilder.AppendLine(CreateMarkdownFromXMLTags(symbolDict, xml.Element("returns")));
                    stringBuilder.AppendLine();
                }

                var exceptions = xml.Elements("except");

                if (exceptions.Any())
                {
                    stringBuilder.AppendLine("## Exceptions");
                    stringBuilder.AppendLine();

                    stringBuilder.AppendLine("|Type|Description|");
                    stringBuilder.AppendLine("|:---|:---|");

                    foreach (var exception in exceptions)
                    {
                        var cref = exception.Attribute("cref")?.Value;
                        var exceptionSymbol = symbolDict[cref];

                        stringBuilder.Append("|");
                        stringBuilder.Append(GetMarkdownLink(exceptionSymbol, configuration));
                        stringBuilder.Append("|");
                        stringBuilder.Append(CreateMarkdownFromXMLTags(symbolDict, exception).Replace("\n", " ").Trim());
                        stringBuilder.AppendLine("|");
                    }

                    stringBuilder.AppendLine();
                }

                var children = symbolDict
                    .Values
                    .Where(s => s.ContainerID == symbol.DocumentationID && HTMLRenderer.GetTypeName(s, plural: true, out _))
                    .GroupBy(s => HTMLRenderer.GetTypeName(s, plural: true, out var typeName) ? typeName : "INVALID");

                if (children.Any())
                {
                    foreach (var group in children.OrderBy(group => group.Key))
                    {
                        string groupName = group.Key;

                        stringBuilder.AppendLine($"## {groupName}");
                        stringBuilder.AppendLine();

                        var shouldHaveType = groupName.ToUpperInvariant() != "CONSTRUCTORS";

                        if(shouldHaveType)
                        {
                            stringBuilder.AppendLine("|Type|Name|Summary|");
                            stringBuilder.AppendLine("|:---|:---|:---|");
                        }
                        else
                        {
                            stringBuilder.AppendLine("|Name|Summary|");
                            stringBuilder.AppendLine("|:---|:---|");
                        }

                        foreach (var childSymbol in group.OrderBy(s => s.DocumentationID))
                        {
                            var childDocs = XElement.Parse(childSymbol.DocumentationXml);
                            var childSummary = CreateMarkdownFromXMLTags(symbolDict, childDocs.Element("summary"))
                                .Replace("\r\n", ". ")
                                .Replace("\n", ". ").Trim();

                            if(childSummary.StartsWith(". "))
                            {
                                childSummary = childSummary.Substring(1);
                            }

                            var link = GetMarkdownLink(childSymbol, configuration, (configuration.OutputMemberFiles ? "./" : "#") +
                                childSymbol.AnchorName);

                            if(shouldHaveType)
                            {
                                var typeString = GetTypeLinkString(childSymbol.Symbol, symbolDict, configuration);

                                stringBuilder.AppendLine($"|{typeString}" +
                                    $"|{link}|{EscapeMarkdown(childSummary)}|");
                            }
                            else
                            {
                                stringBuilder.AppendLine($"|{link}|{EscapeMarkdown(childSummary)}|");
                            }
                        }

                        stringBuilder.AppendLine();
                    }
                }

                var seeAlsos = xml.Elements("seealso");

                if (seeAlsos.Any())
                {
                    stringBuilder.AppendLine("## See Also");
                    stringBuilder.AppendLine();

                    foreach (var seeAlso in seeAlsos)
                    {
                        var href = seeAlso.Attribute("href");
                        var cref = seeAlso.Attribute("cref");

                        if (cref != null)
                        {
                            var seeAlsoSymbol = symbolDict[cref.Value.ToString()];
                            stringBuilder.Append("* " + GetMarkdownLink(seeAlsoSymbol, configuration, asCode: false, useFullDisplayName: true));

                            if (seeAlsoSymbol.DocumentationID.StartsWith("!:") == false)
                            {
                                var seeAlsoSummary = XElement.Parse(seeAlsoSymbol.DocumentationXml).Element("summary");

                                string seeAlsoSummaryText = CreateMarkdownFromXMLTags(symbolDict, seeAlsoSummary);

                                seeAlsoSummaryText = seeAlsoSummaryText.Replace("\n", " ").Trim();
                                stringBuilder.Append(": " + seeAlsoSummaryText);
                            }

                            stringBuilder.AppendLine();
                        }
                        else if (href != null)
                        {
                            var linkText = string.Join(" ", seeAlso.Nodes().Select(n => n.ToString()));

                            stringBuilder.AppendLine($"* [{EscapeMarkdown(linkText)}]({href})");
                        }
                    }

                    stringBuilder.AppendLine();
                }

                if(!configuration.OutputMemberFiles)
                {
                    foreach (var group in children.OrderBy(group => group.Key))
                    {
                        string groupName = group.Key;

                        stringBuilder.AppendLine($"## <a id='{groupName}-detail'> {groupName} </a>");
                        stringBuilder.AppendLine();

                        var groupChildren = group.OrderBy(s => s.DocumentationID).ToArray();

                        for (var i = 0; i < groupChildren.Length; i++)
                        {
                            var childSymbol = groupChildren[i];

                            var childDocs = XElement.Parse(childSymbol.DocumentationXml);

                            var localTypeName = GetTypeLinkString(childSymbol.Symbol, symbolDict, configuration);

                            var shouldHaveType = groupName.ToUpperInvariant() != "CONSTRUCTORS";

                            if(!shouldHaveType)
                            {
                                localTypeName = "";
                            }

                            switch (groupName.ToUpperInvariant())
                            {
                                case "CONSTRUCTORS":
                                case "METHODS":

                                    stringBuilder.AppendLine($"### <a id='{childSymbol.AnchorName}'>{localTypeName} " +
                                        FormatMethodName(childSymbol.Symbol, symbol.Symbol, symbolDict, configuration) + "</a>"
                                    );

                                    break;

                                default:

                                    stringBuilder.AppendLine($"### <a id='{childSymbol.AnchorName}'>{localTypeName} " +
                                        EscapeMarkdownCharacters(childSymbol.Symbol.Name) + "</a>");

                                    break;
                            }

                            HandleChild(childDocs);

                            if(i + 1 < groupChildren.Length)
                            {
                                stringBuilder.AppendLine();

                                stringBuilder.AppendLine("---");

                                stringBuilder.AppendLine();
                            }
                        }

                        stringBuilder.AppendLine();
                    }
                }

                outputs.Add(new MarkdownOutput { Path = path, Content = stringBuilder.ToString(), Title = title });
            }

            return outputs;
        }

        private static string EscapeMarkdown(string input, bool escapeCharacters = false)
        {
            string v = System.Text.RegularExpressions.Regex.Replace(input, "^[ ]+", string.Empty, System.Text.RegularExpressions.RegexOptions.Multiline);

            if (escapeCharacters)
            {
                v = EscapeMarkdownCharacters(v);
            }

            return v;
        }

        private static string CreateMarkdownFromXMLTags(DefaultDictionary<string, DocumentedSymbol> symbolDict, XElement element)
        {
            IEnumerable<XNode> htmlNodes = HTMLRenderer.CreateHTMLFromXMLTags(element, symbolDict, (anchor) => anchor + ".md").Nodes();

            // Replace all <p style="x"> nodes with GitBook-style Hint blocks
            foreach (var node in htmlNodes) {
                if (node is XElement ele) {
                    foreach (var styledPTag in ele.DescendantsAndSelf("p").Where(e => e.Attribute("style") != null).ToList()) {
                        // Get the style attribute's value
                        XAttribute styleAttribute = styledPTag.Attribute("style");
                        var style = styleAttribute.Value;

                        // Remove the attribute from the HTML
                        styleAttribute.Remove();

                        var sb = new System.Text.StringBuilder();
                        sb
                            .Append(System.Environment.NewLine)
                            .Append("{% hint style=\"")
                            .Append(style)
                            .Append("\" %}")
                            .Append(System.Environment.NewLine);

                        var sb2 = new System.Text.StringBuilder();
                        sb2
                            .Append(System.Environment.NewLine)
                            .Append("{% endhint %}")
                            .Append(System.Environment.NewLine);

                        string startOfHint = sb.ToString();
                        string endOfHint = sb2.ToString();

                        styledPTag.AddFirst(startOfHint);
                        styledPTag.Add(endOfHint);
                    }
                }
            }

            string value = string.Join(" ", htmlNodes.Select(s => s.ToString()));

            // Strip leading whitespace from lines
            return EscapeMarkdown(value);
        }

        /// <summary>
        /// Returns Markdown for creating a callout of the specified type.
        /// </summary>
        /// <param name="type">The type of the callout. May be "info" or "warning".</param>
        /// <param name="label">The text to include in the callout.</param>
        /// <returns>The markdown for the callout.</returns>
        private static string CreateCallout(string type, string label)
        {
            var lines = new[] {
                $@"{{% hint style=""{type}"" %}}",
                label,
                "{% endhint %}"
            };
            return string.Join(System.Environment.NewLine, lines);
        }
    }
}