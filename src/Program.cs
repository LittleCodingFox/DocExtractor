using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.MSBuild;
using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;
using ISymbol = Microsoft.CodeAnalysis.ISymbol;

namespace DocExtractor
{
    public enum OutputFormat
    {
        HTML, Markdown, XML
    }

    class Program
    {
        static readonly JsonSerializerOptions SerializationOptions = new JsonSerializerOptions
        {
            AllowTrailingCommas = true,
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
            Converters = {
                new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
            }
        };

        static void Main(string[] args)
        {
            var rootCommand = new RootCommand();

            var buildCommand = new Command("build", "Builds documentation.");
            buildCommand.AddArgument(
                new Argument<FileInfo>("config", "The configuration file.")
                    .ExistingOnly()
            );

            buildCommand.Handler = CommandHandler.Create<FileInfo>(BuildDocumentation);

            var createCommand = new Command("create", "Create a new configuration file.");
            createCommand.AddArgument(
                new Argument<string>("path", "The path to create a new configurataion file at")
                    .LegalFilePathsOnly()
            );

            createCommand.Handler = CommandHandler.Create<string>(CreateConfiguration);

            buildCommand.Handler = CommandHandler.Create<FileInfo>(BuildDocumentation);

            rootCommand.Add(createCommand);
            rootCommand.Add(buildCommand);

            rootCommand.Invoke(args);
        }

        private static void CreateConfiguration(string path)
        {
            var defaultConfiguration = new Configuration
            {
                OutputFolder = Directory.GetCurrentDirectory()
            };

            var output = JsonSerializer.Serialize(defaultConfiguration, SerializationOptions);

            File.WriteAllText(path, output);
        }

        private static void BuildDocumentation(FileInfo config)
        {
            var configDir = Path.GetDirectoryName(config.FullName);
            Directory.SetCurrentDirectory(configDir);

            var configurationJSON = File.ReadAllText(config.FullName);
            var configuration = JsonSerializer.Deserialize<Configuration>(configurationJSON, SerializationOptions);
            BuildDocumentation(configuration);
        }

        /// <summary>
        /// Extracts documentation from a project.
        /// </summary>
        /// <param name="projectFile">The path to the project file.</param>
        /// <param name="outputHTML">Whether to output HTML. If false, outputs XML.</param>
        /// <param name="onlyPublic">Whether to only include public members.</param>
        static void BuildDocumentation(Configuration configuration)
        {
            var projectFiles = configuration.Projects;

            if (projectFiles.Count() == 0)
            {
                Console.Error.WriteLine("Specify at least one project in \"projects\"");
                Environment.Exit(1);
            }

            if (configuration.MSBuildPath == null)
            {
                var instances = MSBuildLocator.QueryVisualStudioInstances().FirstOrDefault();

                if (instances == null)
                {
                    Console.Error.WriteLine($"Can't find any MSBuild instances! Provide one in the {nameof(Configuration.MSBuildPath)} property in the configuration JSON.");
                    Environment.Exit(1);
                }

                MSBuildLocator.RegisterInstance(instances);
            }
            else
            {
                MSBuildLocator.RegisterMSBuildPath(configuration.MSBuildPath);
            }

            var assemblySymbols = new Dictionary<string, List<DocumentedSymbol>>();

            foreach (var path in projectFiles)
            {
                if (File.Exists(path) == false)
                {
                    Console.Error.WriteLine($"{path} is not a valid file.");
                    System.Environment.Exit(1);
                }

                var file = new FileInfo(path);

                Project project = GetProjectFromPath(file.FullName);

                if (project == null)
                {
                    Console.Error.WriteLine($"{path} is not a valid project file.");
                    System.Environment.Exit(1);
                }

                var thisProjectSymbols = GenerateDocumentedSymbols(project, configuration).ToList();

                assemblySymbols[project.AssemblyName] = thisProjectSymbols;
            }

            if (!Directory.Exists(configuration.OutputFolder))
                Directory.CreateDirectory(configuration.OutputFolder);

            switch (configuration.OutputFormat)
            {
                case OutputFormat.HTML:
                    {
                        foreach (var pair in assemblySymbols)
                        {
                            var xml = new XElement("doc",
                                   assemblySymbols.Select(kv =>
                                   new XElement("assembly",
                                           new XElement("name",
                                               kv.Key
                                           ),
                                           new XElement("members",
                                               kv.Value.Select(symbol =>
                                                   XElement.Parse(symbol.DocumentationXml)
                                               )))

                                   )
                               );

                            var symbols = pair.Value;
                            var html = HTMLRenderer.GenerateHTMLForDocXML(xml, symbols).ToString();

                            var outPath = Path.Join(configuration.OutputFolder, pair.Key + ".html");

                            File.WriteAllText(outPath, html);
                            Console.WriteLine(outPath);
                        }
                        break;
                    }

                case OutputFormat.XML:
                    {
                        foreach (var pair in assemblySymbols)
                        {
                            var xml = new XElement("doc",
                                assemblySymbols.Select(kv =>
                                new XElement("assembly",
                                        new XElement("name",
                                            kv.Key
                                        ),
                                        new XElement("members",
                                            kv.Value.Select(symbol =>
                                                XElement.Parse(symbol.DocumentationXml)
                                            )))

                                )
                            );

                            var symbols = pair.Value;
                            var outPath = Path.Join(configuration.OutputFolder, pair.Key + ".xml");

                            File.WriteAllText(outPath, xml.ToString());
                            Console.WriteLine(outPath);
                        }
                        break;
                    }
                case OutputFormat.Markdown:
                    {
                        IEnumerable<DocumentedSymbol> documentedSymbols = assemblySymbols.SelectMany(kv => kv.Value);
                        var outputs = MarkdownRenderer.GenerateMarkdownForDocXML(documentedSymbols, configuration);

                        var summary = MarkdownRenderer.GenerateMarkdownTableOfContentsForDocXML(documentedSymbols, configuration);

                        var summaryPath = "SUMMARY.md";
                        outputs.Add(new MarkdownOutput { Path = summaryPath, Content = summary, Title = "Summary" });

                        var undocumentedSB = new System.Text.StringBuilder();
                        var symbolsWithUndocumentedElements = documentedSymbols.Where(s => s.ContainsUndocumentedElements);

                        if (configuration.OutputFrontMatter)
                        {
                            var slug = string.Join("/", configuration.SlugPrefix, "undocumented");
                            undocumentedSB.AppendLine("---");
                            undocumentedSB.AppendLine($"title: Undocumented");
                            undocumentedSB.AppendLine($"slug: {slug}");
                            undocumentedSB.AppendLine("---");
                        }

                        undocumentedSB.AppendLine($"# Undocumented Items");
                        undocumentedSB.AppendLine();

                        int undocumentedCount = symbolsWithUndocumentedElements.Count();
                        int documentedCount = documentedSymbols.Count();

                        float undocumentedFraction = 1f - ((float)undocumentedCount / (float)documentedCount);

                        undocumentedSB
                            .Append(undocumentedCount)
                            .Append(" items without documentation (of ")
                            .Append(documentedCount)
                            .Append(" total; ")
                            .AppendFormat("{0:F0}", undocumentedFraction  * 100f)
                            .AppendLine("% documented).");
                            
                        undocumentedSB.AppendLine();
                        foreach (var symbol in symbolsWithUndocumentedElements)
                        {
                            var link = MarkdownRenderer.GetMarkdownLink(symbol, configuration, useFullDisplayName: true);
                            undocumentedSB.Append($"* {link}");
                            undocumentedSB.Append(": ");
                            undocumentedSB.AppendLine(string.Join(", ", symbol.UndocumentedElementNames));
                        }

                        var undocumentedPath = "UNDOCUMENTED.md";

                        outputs.Add(new MarkdownOutput { Path = undocumentedPath, Content = undocumentedSB.ToString(), Title = "Undocumented" });

                        foreach (var output in outputs)
                        {
                            var path = Path.Join(configuration.OutputFolder, output.Path);
                            File.WriteAllText(path, output.Content);
                            Console.WriteLine(path);
                        }

                        if (configuration.OutputJsonIndex)
                        {
                            JSONIndex.Generate(configuration, outputs);
                        }

                        break;
                    }
            }
        }

        private static IEnumerable<DocumentedSymbol> GenerateDocumentedSymbols(Project project, Configuration configuration)
        {
            var compilation = project
                .WithParseOptions(
                    CSharpParseOptions.Default
                    .WithPreprocessorSymbols(configuration.PreprocessorSymbols)
                )
                .GetCompilationAsync().Result as CSharpCompilation;
            
            var output = new List<DocumentedSymbol>();

            var excludeRegexes = configuration.ExcludeRegexes.Select(regexText => new System.Text.RegularExpressions.Regex(regexText));

            var workspace = new AdhocWorkspace();

            foreach (var tree in compilation.SyntaxTrees)
            {
                SemanticModel model = compilation.GetSemanticModel(tree, true);

                var walker = new DeclarationWalker(model, (node, symbol) =>
                {
                    // Don't do anything if this symbol isn't public. Walk up
                    // the chain of containing symbols, and stop if any are not
                    // public.
                    var currentSymbol = symbol;
                    while (currentSymbol != null)
                    {
                        if (currentSymbol.DeclaredAccessibility != Accessibility.Public && node is not NamespaceDeclarationSyntax)
                        {
                            return;
                        }
                        if (SymbolEqualityComparer.Default.Equals(symbol.ContainingSymbol, currentSymbol))
                        {
                            break;
                        }
                        currentSymbol = symbol.ContainingSymbol;
                    }

                    var documentSymbol = new DocumentedSymbol();

                    documentSymbol.Syntax = node;

                    string doc = string.Empty;
                    if (symbol.GetDocumentationCommentXml() != null)
                    {
                        doc = DocumentationCommentExtractor.GetDocumentationComment(symbol, null, compilation, null, true, true);
                    }

                    // If this symbol is a namespace, it won't have any documentation comments. We'll create one.
                    if (node is NamespaceDeclarationSyntax)
                    {
                        if (configuration.NamespaceSummaries.TryGetValue(symbol.ToDisplayString(), out var summary))
                        {
                            doc = "<doc><summary>" + summary + "</summary></doc>";
                        }
                    }

                    if (string.IsNullOrEmpty(doc) == false)
                    {
                        try
                        {
                            var element = XElement.Parse(doc);
                            documentSymbol.DocumentationID = element.Attribute("name")?.Value ?? null;
                        }
                        catch (XmlException)
                        {
                        }
                    }

                    if (documentSymbol.DocumentationID == null)
                    {
                        // We didn't have a documentation ID from the doc
                        // comment, so we'll try and generate one.
                        try
                        {
                            documentSymbol.DocumentationID = DocumentationCommentId.CreateDeclarationId(symbol);
                        }
                        catch (System.InvalidOperationException)
                        {
                            // We don't have a declaration ID for this, so we
                            // can't document its declaration.
                            return;
                        }
                    }

                    // Don't do anything if this symbol matches an exclude regex
                    foreach (var excludeRegex in excludeRegexes)
                    {
                        if (excludeRegex.IsMatch(documentSymbol.DocumentationID.Substring(2)))
                        {
                            // Skip it.
                            return;
                        }
                    }

                    // If this symbol is a type, and it has a base type, record
                    // that information
                    if (symbol is ITypeSymbol typeSymbol && typeSymbol.BaseType != null)
                    {
                        var baseTypeDocumentID = DocumentationCommentId.CreateDeclarationId(typeSymbol.BaseType);
                        documentSymbol.BaseTypeID = baseTypeDocumentID;
                    }

                    documentSymbol.Symbol = symbol;

                    var parent = symbol.ContainingSymbol;

                    if (parent != null)
                    {
                        documentSymbol.ContainerID = DocumentationCommentId.CreateDeclarationId(parent);
                    }

                    doc = PopulateMissingDocumentationElements(doc, node, documentSymbol.DocumentationID, model, out IEnumerable<string> undocumentedElements);

                    documentSymbol.UndocumentedElementNames.AddRange(undocumentedElements);

                    documentSymbol.DocumentationXml = string.IsNullOrEmpty(doc) ? string.Empty : doc;

                    output.Add(documentSymbol);
                });

                walker.Visit(tree.GetRoot());
            }

            output = output.Distinct(s => s.DocumentationID).ToList();

            var emptyAttributeList = new SyntaxList<AttributeListSyntax>();
            var missingOpenBrace = SyntaxFactory.MissingToken(SyntaxKind.OpenBraceToken);
            var missingCloseBrace = SyntaxFactory.MissingToken(SyntaxKind.CloseBraceToken);
            //var emptyTokenList = new SyntaxTokenList();

            foreach (var symbol in output)
            {
                string name;
                SyntaxNode declarationSyntax;

                SyntaxList<MemberDeclarationSyntax> emptyMemberList = new SyntaxList<MemberDeclarationSyntax>();

                var containingType = symbol.Symbol.ContainingType;
                switch (symbol.Syntax)
                {
                    case MethodDeclarationSyntax method:
                        name = method.Identifier.ValueText;

                        name += "(";

                        name += string.Join(",", method.ParameterList.Parameters.Select(p => p.Type.ToString()));

                        name += ")";

                        declarationSyntax = method.WithExpressionBody(null).WithBody(null).WithAttributeLists(emptyAttributeList);

                        break;
                    case FieldDeclarationSyntax field:
                        name = field.Declaration.Variables.First().Identifier.ValueText;

                        declarationSyntax = field.WithAttributeLists(emptyAttributeList);
                        break;
                    case PropertyDeclarationSyntax property:
                        name = property.Identifier.ValueText;

                        AccessorListSyntax accessorList = property.AccessorList;

                        if (accessorList == null)
                        {
                            accessorList = SyntaxFactory.AccessorList();
                        }

                        var accessors = accessorList.Accessors
                            .Select(a => a
                                .WithBody(null)
                                .WithExpressionBody(null)
                                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                            );

                        if (accessors == null || accessors.Count() == 0)
                        {
                            // Create a new 'get' accessor
                            accessors = new[] { SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration) };
                        }

                        accessorList = accessorList?.WithAccessors(new SyntaxList<AccessorDeclarationSyntax>(accessors)).WithoutTrivia();

                        declarationSyntax = property
                            .WithInitializer(null)
                            .WithExpressionBody(null)
                            .WithAttributeLists(emptyAttributeList)
                            .WithAccessorList(accessorList);

                        break;
                    case ClassDeclarationSyntax @class:
                        name = @class.Identifier.ValueText;
                        declarationSyntax = @class
                            .WithMembers(emptyMemberList)
                            .WithAttributeLists(emptyAttributeList)
                            .WithOpenBraceToken(missingOpenBrace)
                            .WithCloseBraceToken(missingCloseBrace)
                            .WithModifiers(new SyntaxTokenList(@class.Modifiers.Where(m => m.Text != "partial")))
                            ;

                        break;
                    case StructDeclarationSyntax @struct:
                        name = @struct.Identifier.ValueText;
                        declarationSyntax = @struct
                            .WithMembers(emptyMemberList)
                            .WithAttributeLists(emptyAttributeList)
                            .WithOpenBraceToken(missingOpenBrace)
                            .WithCloseBraceToken(missingCloseBrace);

                        break;
                    case InterfaceDeclarationSyntax @interface:
                        name = @interface.Identifier.ValueText;
                        declarationSyntax = @interface
                            .WithMembers(emptyMemberList)
                            .WithAttributeLists(emptyAttributeList)
                            .WithOpenBraceToken(missingOpenBrace)
                            .WithCloseBraceToken(missingCloseBrace);
                        break;
                    case NamespaceDeclarationSyntax @namespace:
                        name = @namespace.Name.ToString();
                        declarationSyntax = @namespace.WithMembers(emptyMemberList)
                            .WithOpenBraceToken(missingOpenBrace)
                            .WithCloseBraceToken(missingCloseBrace); ;
                        break;
                    case VariableDeclaratorSyntax variable:
                        name = variable.Identifier.ValueText;
                        declarationSyntax = variable;
                        break;
                    case ConstructorDeclarationSyntax constructor:
                        name = (constructor.Parent as BaseTypeDeclarationSyntax).Identifier.ValueText;

                        name += "(";

                        name += string.Join(",", constructor.ParameterList.Parameters.Select(p => p.Type.ToString()));

                        name += ")";
                        declarationSyntax = constructor
                            .WithBody(null)
                            .WithExpressionBody(null)
                            .WithAttributeLists(emptyAttributeList)
                            .WithInitializer(null);

                        break;
                    case EnumDeclarationSyntax @enum:
                        name = @enum.Identifier.ValueText;

                        // Create a member list based on our members with no attributes
                        var memberSyntax = new SeparatedSyntaxList<EnumMemberDeclarationSyntax>().AddRange(@enum.Members.Select(m => m
                            .WithoutTrivia()
                            .WithAttributeLists(emptyAttributeList)));

                        declarationSyntax = @enum
                            .WithMembers(memberSyntax)
                            .WithAttributeLists(emptyAttributeList);

                        break;
                    case EnumMemberDeclarationSyntax enumMember:
                        name = enumMember.Identifier.ValueText;
                        declarationSyntax = enumMember.WithAttributeLists(emptyAttributeList);
                        break;
                    case DelegateDeclarationSyntax delegateMember:
                        name = delegateMember.Identifier.ValueText;
                        declarationSyntax = delegateMember.WithAttributeLists(emptyAttributeList);
                        break;
                    case IndexerDeclarationSyntax indexer:
                        var indexerDeclarationSyntax = SyntaxFactory.IndexerDeclaration(indexer.Type).WithParameterList(indexer.ParameterList);
                        declarationSyntax = indexerDeclarationSyntax;

                        name = "this" + indexer.ParameterList.ToString();
                        break;
                    default:
                        // Fallback: use the ugly DocumentationID
                        name = symbol.DocumentationID;
                        declarationSyntax = null;
                        break;
                }

                var ancestorChain = new List<ISymbol>();
                var currentSymbol = symbol.Symbol;

                while (currentSymbol != null)
                {
                    ancestorChain.Add(currentSymbol);
                    currentSymbol = currentSymbol.ContainingType;
                }

                var currentNamespace = symbol.Symbol.ContainingNamespace;

                while (currentNamespace != null && currentNamespace.Name.Length > 0)
                {
                    ancestorChain.Add(currentNamespace);
                    currentNamespace = currentNamespace.ContainingNamespace;
                }

                var anchor = string.Join('.', ancestorChain.Reverse<ISymbol>().Select(s => s.Name)).ToLowerInvariant();

                symbol.DisplayName = name;
                symbol.AnchorName = anchor;

                symbol.FullDisplayName = name;

                if (containingType != null)
                {
                    switch (symbol.Syntax)
                    {
                        case MethodDeclarationSyntax:
                        case PropertyDeclarationSyntax:
                        case FieldDeclarationSyntax:
                        case VariableDeclarationSyntax:
                        case IndexerDeclarationSyntax:
                        case EnumMemberDeclarationSyntax:
                            symbol.FullDisplayName = containingType.Name + "." + name;
                            break;
                    }
                }

                if (declarationSyntax != null)
                {
                    declarationSyntax = Formatter.Format(declarationSyntax, workspace);
                }

                symbol.Declaration = declarationSyntax?.ToString().Trim() ?? "(no declaration available)";
            }

            // We may have symbols with the same anchor (due to overloads,
            // or case-sensitivity), so make these unique by suffixing a
            // counter
            foreach (var anchorGroup in output.GroupBy(s => s.AnchorName))
            {
                if (anchorGroup.Count() > 1)
                {
                    int count = 1;
                    foreach (var symbol in anchorGroup)
                    {
                        symbol.AnchorName += "-" + count;
                        count += 1;
                    }
                }
            }

            return output;
        }

        private static string PopulateMissingDocumentationElements(string doc, SyntaxNode node, string documentationID, SemanticModel model, out IEnumerable<string> undocumentedElementNames)
        {
            XElement xml;

            var undocumentedElementNameList = new List<string>();

            if (string.IsNullOrEmpty(doc) == false)
            {
                try
                {
                    xml = XElement.Parse(doc);
                }
                catch
                {
                    xml = new XElement("member", new XAttribute("name", documentationID));
                }
            }
            else
            {
                xml = new XElement("member", new XAttribute("name", documentationID));
            }

            const string NotDocumentedText = "";

            // All documentated symbols have a summary; if one is missing, add it now
            if (xml.Element("summary") == null)
            {
                xml.Add(new XElement("summary", NotDocumentedText));
                undocumentedElementNameList.Add("summary");
            }

            // If this is a method or a constructor, check to see if we have XML
            // documentation for the following elements:
            // - every parameter
            // - every type parameter
            // - the return type
            //
            // thought: is it possible/useful to walk the method body looking
            // for 'throw' statements and use them to check to see if we need to
            // document thrown exceptions?
            //
            // thought 2: would it then be possible/useful to warn if a method
            // that this method calls may throw an exception and this method
            // doesn't catch it? i.e. following the java rules for exceptions

            ParameterListSyntax parameterList = null;
            TypeParameterListSyntax typeParameterList = null;

            if (node is MethodDeclarationSyntax method)
            {
                parameterList = method.ParameterList;
                typeParameterList = method.TypeParameterList;
            }
            else if (node is ConstructorDeclarationSyntax constructor)
            {
                parameterList = constructor.ParameterList;
                typeParameterList = null;
            }
            else if (node is DelegateDeclarationSyntax @delegate)
            {
                parameterList = @delegate.ParameterList;
                typeParameterList = @delegate.TypeParameterList;
            }

            if (parameterList != null)
            {
                foreach (var parameter in parameterList.Parameters)
                {
                    string name = parameter.Identifier.Text;

                    // Get the documentation for this parameter
                    var paramDoc = xml.Elements("param")
                        .Where(e => e.Attribute("name")?.Value == name).FirstOrDefault();

                    if (paramDoc == null)
                    {
                        // Not provided! Document it now.
                        paramDoc = new XElement("param", new XAttribute("name", name), NotDocumentedText);
                        xml.Add(paramDoc);
                        undocumentedElementNameList.Add($"parameter \"{name}\"");
                    }

                    // Ensure that this parameter has a 'type' attribute.
                    // This is not used in IDE contexts, because IDEs
                    // generally figure this out via reading the source, but
                    // it's useful for documentation.
                    var parameterType = parameter.Type;
                    var parameterTypeSymbol = model.GetTypeInfo(parameterType).Type;

                    string parameterTypeName = parameterTypeSymbol.ToMinimalDisplayString(model, 0, SymbolDisplayFormat.MinimallyQualifiedFormat);
                    string parameterTypeID;

                    // Arrays can't be referenced by name, so we'll instead
                    // refer to the array element type.
                    if (parameterTypeSymbol is IArrayTypeSymbol arrayTypeSymbol)
                    {
                        parameterTypeSymbol = arrayTypeSymbol.ElementType;
                    }

                    if (parameterTypeSymbol.CanBeReferencedByName)
                    {
                        parameterTypeID = parameterTypeSymbol.GetDocumentationCommentId();
                    }
                    else
                    {
                        parameterTypeID = null;
                    }

                    paramDoc.SetAttributeValue("typeName", parameterTypeName);
                    paramDoc.SetAttributeValue("typeID", parameterTypeID);
                }
            }

            var nodeIsMethodWithNonVoidReturnType =
                node is MethodDeclarationSyntax methodSyntax
                && methodSyntax.ReturnType is PredefinedTypeSyntax predefinedType
                && predefinedType.Keyword.IsKind(SyntaxKind.NullKeyword);

            if (nodeIsMethodWithNonVoidReturnType || node is IndexerDeclarationSyntax)
            {
                if (xml.Element("returns") == null)
                {
                    // Add a node for the return type
                    xml.Add(new XElement("returns", NotDocumentedText));
                    undocumentedElementNameList.Add("return");
                }
            }

            if (typeParameterList != null)
            {
                foreach (var typeParameter in typeParameterList.Parameters)
                {
                    string name = typeParameter.Identifier.Text;

                    var paramDoc = xml.Elements("typeparam")
                        .Where(e => e.Attribute("name")?.Value == name);

                    if (paramDoc == null)
                    {
                        xml.Add(new XElement("typeparam", new XAttribute("name", name), NotDocumentedText));
                        undocumentedElementNameList.Add($"type parameter \"{name}\"");
                    }
                }
            }

            undocumentedElementNames = undocumentedElementNameList;

            return xml.ToString();
        }

        static Project GetProjectFromPath(string path)
        {
            MSBuildWorkspace workspace = MSBuildWorkspace.Create();

            return workspace.OpenProjectAsync(path).Result;
        }

        public static bool IsValidSyntaxForLinking(SyntaxNode node, Configuration configuration, bool isNavigation)
        {
            if(configuration.OutputMemberFiles)
            {
                return true;
            }

            if(node is EnumDeclarationSyntax && !configuration.OutputFileForEnumerationMembers)
            {
                return false;
            }

            if(!isNavigation && (node is FieldDeclarationSyntax ||
                node is PropertyDeclarationSyntax ||
                node is MethodDeclarationSyntax ||
                node is ConstructorDeclarationSyntax))
            {
                return true;
            }

            return node is ClassDeclarationSyntax ||
                node is StructDeclarationSyntax ||
                node is EnumDeclarationSyntax ||
                node is InterfaceDeclarationSyntax;
        }

        public static string GetSymbolType(ISymbol symbol)
        {
            var baseType = "";

            switch (symbol)
            {
                case IFieldSymbol f:

                    baseType = f.Type.ToDisplayString(NullableFlowState.None);

                    break;

                case IPropertySymbol p:

                    baseType = p.Type.ToDisplayString(NullableFlowState.None);

                    break;

                case IMethodSymbol m:

                    baseType = m.ReturnType.ToDisplayString(NullableFlowState.None);

                    break;

                case IParameterSymbol p:

                    baseType = p.Type.ToDisplayString(NullableFlowState.None);

                    break;
            }

            return baseType;
        }

        internal static string NormalizeTypeName(string typeName)
        {
            if (typeName.Contains('.'))
            {
                typeName = typeName.Split('.').Last();
            }

            return typeName;
        }
    }

    public static class DistinctExtension
    {
        public static IEnumerable<T> Distinct<T, TKey>(this IEnumerable<T> list, Func<T, TKey> lookup)
        {
            return list.Distinct(new DelegateEqualityComparer<T, TKey>(lookup));
        }
    }

    class DelegateEqualityComparer<T, TKey> : IEqualityComparer<T>
    {
        Func<T, TKey> lookup;

        public DelegateEqualityComparer(Func<T, TKey> lookup)
        {
            this.lookup = lookup;
        }

        public bool Equals(T x, T y)
        {
            return lookup(x).Equals(lookup(y));
        }

        public int GetHashCode(T obj)
        {
            return lookup(obj).GetHashCode();
        }
    }
}
