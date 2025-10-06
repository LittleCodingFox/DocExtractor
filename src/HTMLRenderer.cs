using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text.RegularExpressions;

namespace DocExtractor
{
    public static class HTMLRenderer
    {

        public static XElement GenerateHTMLForDocXML(XElement xml, IEnumerable<DocumentedSymbol> documentedSymbols)
        {
            var contentsElem = new XElement("div", new XAttribute("class", "container"));

            var groups = documentedSymbols.GroupBy(symbol => symbol.ContainerID);

            var types = documentedSymbols.Where(s => s.DocumentationID.StartsWith("T:"));

            var symbolDict = RendererUtility.CreateSymbolDictionary(documentedSymbols);

            var globalNavList = new XElement("dl");

            contentsElem.Add(new XElement("nav", new XAttribute("class", "types"), globalNavList));

            foreach (var member in types.OrderBy(t => t.Syntax.GetType().Name).ThenBy(t => t.BaseTypeID).ThenBy(t => t.DisplayName))
            {
                XElement typeElement = GenerateHTMLForType(symbolDict, member, out var members);

                // when putting everything in a single page, put the member elements in the type element
                typeElement.Add(members);

                contentsElem.Add(typeElement);

                globalNavList.Add(
                    new XElement("dt",

                        new XElement("a",
                            new XAttribute("href", ReferenceToAnchor(member.AnchorName)),
                            member.DisplayName
                        )
                    ),
                    new XElement("dd",
                        CreateHTMLFromXMLTags(XElement.Parse(member.DocumentationXml).Element("summary"), symbolDict, ReferenceToAnchor).Nodes()
                    )

                    
                );
            }

            var headElem = XElement.Parse(@"
                <head>
                <title>YarnQuests Documentation</title>
                <!-- Required meta tags -->
                <meta charset=""utf-8"" />
                <meta name=""viewport"" content=""width=device-width, initial-scale=1, shrink-to-fit=no"" />

                <link rel=""stylesheet"" href=""https://maxcdn.bootstrapcdn.com/bootstrap/4.0.0/css/bootstrap.min.css"" integrity=""sha384-Gn5384xqQ1aoWXA+058RXPxPg6fy4IWvTNh0E263XmFcJlSAwiGgFAW/dAiS6JXm"" crossorigin=""anonymous"" />

                <style>
                    section {
                        margin-top: 2em;
                        margin-left: 1em;
                    }
                    article {
                        margin-top: 1em;
                        padding-top: 1em;
                        border-top: 1px solid grey;
                    }
                </style>

            </head>
            ");

            contentsElem.AddFirst(new XElement("h1", "YarnQuests Documentation"));

            return new XElement("html",
                headElem,
                new XElement("body", 
                    contentsElem
                )
            );
        }

        internal static XElement GenerateHTMLForType(DefaultDictionary<string, DocumentedSymbol> symbolDict, DocumentedSymbol member, out IEnumerable<XElement> memberElements)
        {

            var memberID = member.DocumentationID;

            var doc = XElement.Parse(member.DocumentationXml);

            var typeElem = new XElement("article");

            typeElem.Add(new XElement("header",
                new XAttribute("id", member.AnchorName),
                new XElement("h2", member.DisplayName)));

            var outputMemberElements = new List<XElement>();

            if (GetTypeName(member, false, out var typeLabel))
            {
                typeElem.Add(new XElement("p",
                    new XAttribute("class", "typeKind"),
                    typeLabel));
            }

            if (member.BaseTypeID != null)
            {
                var baseDocumentedSymbol = symbolDict[member.BaseTypeID];

                XElement link;

                string anchorName = baseDocumentedSymbol.AnchorName;

                if (anchorName != null)
                {
                    link = new XElement("a",
                        new XAttribute("href", ReferenceToAnchor(anchorName)),
                        baseDocumentedSymbol.DisplayName
                    );
                }
                else
                {
                    link = new XElement("code",
                        baseDocumentedSymbol.DisplayName
                    );
                }

                typeElem.Add(
                    new XElement("section",
                        new XAttribute("class", "inheritsFrom"),
                        new XElement("h3", "Inherits from"),
                        link
                    )
                );
            }

            typeElem.Add(
                new XElement("section",
                    new XAttribute("class", "declaration"),
                    new XElement("h2", "Declaration"),
                    new XElement("code",
                        member.Declaration
                    )
                )
            );

            var mainElem = new XElement("main");

            typeElem.Add(mainElem);


            // Add summary, if present
            if (doc.Element("summary") != null)
            {
                mainElem.Add(
                    new XElement("section",
                        new XAttribute("class", "summary"),
                        new XElement("h3", "Summary"),
                        CreateHTMLFromXMLTags(doc.Element("summary"), symbolDict, ReferenceToAnchor).Nodes()
                    )
                );
            }

            // Add remarks, if present
            if (doc.Element("remarks") != null)
            {
                mainElem.Add(
                    new XElement("section",
                        new XAttribute("class", "remarks"),
                        new XElement("h3", "Remarks"),
                        CreateHTMLFromXMLTags(doc.Element("remarks"), symbolDict, ReferenceToAnchor).Nodes()
                    )
                );
            }

            var children = symbolDict.Values.Where(s => s.ContainerID == memberID).GroupBy(s => s.DocumentationID.Substring(0, 1));

            if (children.Count() > 0)
            {

                var navElem = new XElement("nav", new XAttribute("class", "members"));
                typeElem.Add(navElem);

                var navList = new XElement("ul");

                navElem.Add(navList);

                foreach (var group in children)
                {

                    string groupName;

                    switch (group.Key)
                    {
                        case "F": groupName = "Fields"; break;
                        case "P": groupName = "Properties"; break;
                        case "T": groupName = "Types"; break;
                        case "E": groupName = "Events"; break;
                        case "M": groupName = "Methods"; break;
                        default: groupName = "(none)"; break;
                    }
                    navList.Add(new XElement("li", groupName));

                    var groupList = new XElement("ul", new XAttribute("class", groupName.ToLowerInvariant() + "-list"));
                    navList.Add(groupList);



                    foreach (var symbol in group)
                    {
                        XElement symbolElem = GenerateHTMLForMember(symbolDict, symbol, 4);
                        outputMemberElements.Add(symbolElem);

                        var groupListItem = new XElement("dl");

                        groupListItem.Add(new XElement("dt",
                            new XElement("a",
                                new XAttribute("href", ReferenceToAnchor(symbol.AnchorName)),
                                symbol.DisplayName
                            )));

                        groupListItem.Add(new XElement("dd",
                            CreateHTMLFromXMLTags(XElement.Parse(symbol.DocumentationXml).Element("summary"), symbolDict, ReferenceToAnchor)
                        ));

                        groupList.Add(groupListItem);
                    }
                }

            }

            memberElements = outputMemberElements;

            return typeElem;
        }

        public static bool GetTypeName(DocumentedSymbol member, bool plural, out string typeName)
        {
            switch (member.Syntax)
            {
                case ClassDeclarationSyntax:
                    if (member.BaseTypeID?.Substring(1).Equals(":System.Attribute") ?? false) {
                        // This is an attribute!
                        typeName = plural ? "Attributes" : "Attribute";

                        return true;

                    } else {
                        typeName = plural ? "Classes" : "Class";

                        return true;
                    }

                case InterfaceDeclarationSyntax:
                    typeName = plural ? "Interfaces" : "Interface";

                    return true;

                case EnumDeclarationSyntax:
                    typeName = plural ? "Enums" : "Enum";

                    return true;

                case EnumMemberDeclarationSyntax:
                    typeName = plural ? "Members" : "Enumeration Member";

                    return true;

                case StructDeclarationSyntax:
                    typeName = plural ? "Structs" : "Struct";

                    return true;

                case MethodDeclarationSyntax:
                    typeName = plural ? "Methods" : "Method";

                    return true;

                case IndexerDeclarationSyntax:
                    typeName = plural ? "Indexers" : "Indexer";

                    return true;

                case FieldDeclarationSyntax:
                    typeName = plural ? "Fields" : "Field";

                    return true;

                case PropertyDeclarationSyntax:
                    typeName = plural ? "Properties" : "Property";

                    return true;

                case ConstructorDeclarationSyntax:
                    typeName = plural ? "Constructors" : "Constructor";

                    return true;

                case DelegateDeclarationSyntax:
                    typeName = plural ? "Delegates" : "Delegate";

                    return true;

                case NamespaceDeclarationSyntax:
                    typeName = plural ? "Namespaces" : "Namespace";

                    return true;

                default:
                    typeName = null;

                    return false;
            }
        }

        private static string ReferenceToAnchor(string anchor)
        {
            return "#" + anchor;
        }

        private static XElement GenerateHTMLForMember(DefaultDictionary<string, DocumentedSymbol> symbolDict, DocumentedSymbol symbol, int headerLevel = 1, string containerType = "section")
        {
            var symbolDoc = XElement.Parse(symbol.DocumentationXml);
            var symbolID = symbol.DocumentationID;

            var symbolElem = new XElement(containerType, new XAttribute("class", "item"));


            string primaryHeaderLevel = "h" + headerLevel;
            string secondaryHeaderLevel = "h" + (headerLevel + 1);

            symbolElem.Add(
                new XElement("header",
                new XAttribute("id", symbol.AnchorName),
                new XElement(primaryHeaderLevel, symbol.DisplayName)
                ));

            symbolElem.Add(
                new XElement("section",
                    new XAttribute("class", "declaration"),
                    new XElement(secondaryHeaderLevel, "Declaration"),
                    new XElement("code",
                        symbol.Declaration
                    )
                )
            );

            // Add summary, if present
            if (symbolDoc.Element("summary") != null)
            {
                symbolElem.Add(
                    new XElement("section",
                        new XAttribute("class", "summary"),
                        new XElement(secondaryHeaderLevel, "Summary"),
                        CreateHTMLFromXMLTags(symbolDoc.Element("summary"), symbolDict, ReferenceToAnchor).Nodes()
                    )
                );
            }

            // Add remarks, if present
            if (symbolDoc.Element("remarks") != null)
            {
                symbolElem.Add(
                    new XElement("section",
                        new XAttribute("class", "remarks"),
                        new XElement(secondaryHeaderLevel, "Remarks"),
                        CreateHTMLFromXMLTags(symbolDoc.Element("remarks"), symbolDict, ReferenceToAnchor).Nodes()
                    )
                );
            }

            // Add summary, if present
            var paramNodes = symbolDoc.Elements("param");
            if (paramNodes.Count() > 0)
            {
                symbolElem.Add(new XElement("section",
                    new XAttribute("class", "params"),
                    new XElement(secondaryHeaderLevel, "Parameters"),

                    paramNodes.Select(param => new XElement("dl",
                        new XAttribute("class", "param"),
                        new XElement("dt",
                            param.Attribute("name").Value
                        ),
                        new XElement("dd",
                            CreateHTMLFromXMLTags(param, symbolDict, ReferenceToAnchor).Nodes()
                            )
                        ))
                ));
            }

            var typeParamNodes = symbolDoc.Elements("typeparam");
            if (typeParamNodes.Count() > 0)
            {
                symbolElem.Add(new XElement("section",
                    new XAttribute("class", "typeParams"),
                    new XElement(secondaryHeaderLevel, "Type Parameters"),

                    typeParamNodes.Select(typeParam => new XElement("dl",
                        new XAttribute("class", "typeParam"),
                            new XElement("dt",
                            typeParam.Attribute("name").Value
                        ),
                        new XElement("dd",
                            CreateHTMLFromXMLTags(typeParam, symbolDict, ReferenceToAnchor).Nodes()
                            )
                        ))
                ));
            }

            if (symbolDoc.Element("returns") != null)
            {
                symbolElem.Add(new XElement("section",
                    new XAttribute("class", "returns"),
                    new XElement(secondaryHeaderLevel, "Returns"),
                    CreateHTMLFromXMLTags(symbolDoc.Element("returns"), symbolDict, ReferenceToAnchor).Nodes()
                ));
            }

            var seeAlsos = symbolDoc.Elements("seealso");
            if (seeAlsos.Count() > 0)
            {
                symbolElem.Add(new XElement("section",
                    new XAttribute("class", "seealso"),
                    new XElement(secondaryHeaderLevel, "See Also"),

                    new XElement("ul",
                        new XAttribute("class", "seeAlso"),
                        seeAlsos.Select(element =>
                        {

                            var link = new XElement("a");
                            var href = element.Attribute("href");
                            var cref = element.Attribute("cref");

                            if (cref != null)
                            {
                                var symbol = symbolDict[cref.Value];
                                string anchorName = symbol.AnchorName;

                                if (anchorName == null)
                                {
                                    // We don't have an anchor
                                    // for this. Convert it to
                                    // code instead, because
                                    // it's not a link.
                                    link.Name = "code";
                                }
                                else
                                {
                                    // Attach the href to the
                                    // link
                                    href = new XAttribute("href", ReferenceToAnchor(anchorName));
                                    link.Add(href);
                                }
                                link.Add(symbol.DisplayName);
                            }
                            else if (href != null)
                            {
                                // re-use the existing 'href' attribute and include whatever text was present
                                link.Add(element.Nodes());
                            }
                            else
                            {
                                // Neither are present? Then return nothing
                                return new XElement("li", $"(invalid seealso element: {element}");
                            }
                            return new XElement("li", link);
                        }
                        )
                    )
                ));
            }

            var exceptions = symbolDoc.Elements("except");
            if (exceptions.Count() > 0)
            {
                symbolElem.Add(new XElement("section",
                    new XAttribute("class", "except"),
                    new XElement(secondaryHeaderLevel, "Exceptions"),

                    new XElement("dl",
                        new XAttribute("class", "exceptions"),


                        exceptions.SelectMany(exception =>
                        {
                            var cref = exception.Attribute("cref")?.Value;
                            var symbol = symbolDict[cref];

                            var title = new XElement("dt");


                            string anchorName = symbol.AnchorName;
                            if (anchorName != null)
                            {
                                title.Add(new XElement("a",
                                    new XAttribute("href", ReferenceToAnchor(anchorName)),
                                    symbol.DisplayName));
                            }
                            else
                            {
                                title.Add(new XElement("code"), symbol.DisplayName);
                            }

                            var definition = new XElement("dd",
                                exception.Nodes()
                            );

                            return new[] { title, definition };
                        }
                    )
                )));
            }

            return symbolElem;
        }

        /// <summary>
        /// Returns a clone of <paramref name="input"/>, but with tags
        /// replaced with HTML elements.
        /// </summary>
        /// <param name="input">The input XML.</param>
        /// <returns>An HTML version of <paramref name="input"/>.</returns>
        public static XElement CreateHTMLFromXMLTags(XElement input, IDictionary<string, DocumentedSymbol> symbols, System.Func<string,string> anchorReferenceHandler)
        {
            XElement output = new XElement(input);

            // Replace all CDATA with HTML-encoded versions of the same content
            foreach (var cData in output.Descendants().OfType<XCData>().ToList()) {
                cData.ReplaceWith(System.Web.HttpUtility.HtmlEncode(cData.Value));
            }

            foreach (var codeBlock in output.Descendants("code"))
            {
                codeBlock.Name = "pre";
            }

            foreach (var inlineCode in output.Descendants("c"))
            {
                inlineCode.Name = "code";
            }

            // <see langword="true"/> -> <code>true</code>
            foreach (var see in output.Descendants("see").Where(n => n.Attribute("langword") != null))
            {
                see.Name = "code";
                see.Add(see.Attribute("langword").Value);
                see.RemoveAttributes();
            }

            // <see cref="T:SomeType"> -> <a
            // href="T:SomeType">T:SomeType</a>
            //
            // <see href="http://github.com">GitHub</see> -> <a
            // href="http://github.com">GitHub</a>
            foreach (var see in output.Descendants("see"))
            {
                see.Name = "a";

                var cref = see.Attribute("cref");
                var href = see.Attribute("href");

                see.RemoveAttributes();

                if (cref != null)
                {
                    var anchorName = symbols[cref.Value].AnchorName;

                    if (anchorName != null)
                    {
                        see.SetAttributeValue("href", anchorReferenceHandler(anchorName));
                    }
                    else
                    {
                        see.Name = "code";
                    }
                    see.Add(symbols[cref.Value].DisplayName);
                }
                else if (href != null)
                {
                    see.SetAttributeValue("href", href.Value);
                }
            }

            foreach (var para in output.Descendants("para"))
            {
                para.Name = "p";
            }

            foreach (var list in output.Descendants("list"))
            {
                string listType = list.Attribute("type")?.Value ?? "bullet";

                if (listType == "table")
                {
                    // TODO: handle definition lists and tables
                    break;
                }

                switch (listType)
                {
                    case "bullet":
                        list.Name = "ul";
                        break;
                    case "number":
                        list.Name = "ol";
                        break;
                }
                foreach (var item in list.Elements("item"))
                {
                    item.Name = "li";
                }
            }

            foreach (var paramRef in output.Descendants("paramref"))
            {
                paramRef.Name = "code";
                paramRef.Add(paramRef.Attribute("name")?.Value);
                paramRef.Attribute("name")?.Remove();
            }

            foreach (var paramRef in output.Descendants("typeparamref"))
            {
                paramRef.Name = "code";
                paramRef.Add(paramRef.Attribute("name").Value);
                paramRef.Attribute("name").Remove();
            }

            foreach (var example in output.Descendants("example"))
            {
                example.Name = "div";
                example.Add(new XAttribute("class", "example"));
            }

            return output;
        }

    }
}