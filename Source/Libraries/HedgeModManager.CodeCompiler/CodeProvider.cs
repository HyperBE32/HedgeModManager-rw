﻿namespace HedgeModManager.CodeCompiler;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis;
using System.ComponentModel;
using System.Text;
using Properties;
using Foundation;
using PreProcessor;

public class CodeProvider
{
    private static object mLockContext = new object();

    public static MethodDeclarationSyntax LoaderExecutableMethod =
        SyntaxFactory.MethodDeclaration(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.BoolKeyword)), "IsLoaderExecutable")
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression)))
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword), SyntaxFactory.Token(SyntaxKind.StaticKeyword)));
        
    public static UsingDirectiveSyntax[] PredefinedUsingDirectives =
    {
        SyntaxFactory.UsingDirective(SyntaxFactory.IdentifierName("System")),
        SyntaxFactory.UsingDirective(SyntaxFactory.IdentifierName("HMMCodes")),
        SyntaxFactory.UsingDirective(
            SyntaxFactory.Token(SyntaxKind.UsingKeyword),
            SyntaxFactory.Token(SyntaxKind.StaticKeyword), null,
            SyntaxFactory.QualifiedName(SyntaxFactory.IdentifierName("HMMCodes"), SyntaxFactory.IdentifierName("MemoryService")), SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
    };

    public static SyntaxTree[] PredefinedClasses =
    {
        CSharpSyntaxTree.ParseText(Resources.MemoryService),
        CSharpSyntaxTree.ParseText(Resources.Keys)
    };

    public static void TryLoadRoslyn()
    {
        new Thread(() =>
        {
            try
            {
                CompileCodes(Array.Empty<CSharpCode>(), Stream.Null);
            }
            catch { }
        }).Start();
    }

    public static Task CompileCodes<TCollection>(TCollection sources, string assemblyPath, IIncludeResolver? includeResolver = null, params string[] loadsPaths) where TCollection : IEnumerable<CSharpCode>
    {
        lock (mLockContext)
        {
            using var stream = File.Create(assemblyPath);
            return CompileCodes(sources, stream, includeResolver, loadsPaths);
        }
    }

    public static Task CompileCodes<TCollection>(TCollection sources, Stream resultStream, IIncludeResolver? includeResolver = null, params string[] loadPaths) where TCollection : IEnumerable<CSharpCode>
    {
        lock (mLockContext)
        {
            var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true);
            var trees = new List<SyntaxTree>();
            var newLibs = new HashSet<string>();
            var loads = GetLoadAssemblies(sources, loadPaths);

            foreach (var source in sources)
            {
                if (!source.IsExecutable())
                {
                    continue;
                }

                trees.Add(source.CreateSyntaxTree());

                foreach (string reference in source.GetReferences())
                {
                    newLibs.Add(reference);
                }

                foreach (string reference in source.GetImports())
                {
                    newLibs.Add(reference);
                }
            }

            var libs = new HashSet<string>();
            while (newLibs.Count != 0)
            {
                var addedLibs = new List<string>(newLibs.Count);
                foreach (string lib in newLibs)
                {
                    if (libs.Contains(lib))
                    {
                        continue;
                    }

                    var libSource = sources.FirstOrDefault(x => x.Name == lib);
                    if (libSource == null)
                    {
                        throw new Exception($"Unable to find dependency library {lib}");
                    }

                    trees.Add(libSource.CreateSyntaxTree());
                    libs.Add(lib);

                    foreach (string reference in libSource.GetReferences())
                    {
                        addedLibs.Add(reference);
                    }

                    foreach (string reference in libSource.GetImports())
                    {
                        addedLibs.Add(reference);
                    }
                }

                newLibs.Clear();
                newLibs.UnionWith(addedLibs);
            }

            if (RuntimeInformation.FrameworkDescription.StartsWith(".NET Framework"))
            {
                var frameworkRoot = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
                loads.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
                loads.Add(MetadataReference.CreateFromFile(typeof(Component).Assembly.Location));
                loads.Add(MetadataReference.CreateFromFile(typeof(System.Runtime.CompilerServices.DynamicAttribute).Assembly.Location));
                loads.Add(MetadataReference.CreateFromFile(Path.Combine(frameworkRoot, "Microsoft.CSharp.dll")));
            }
            else
            {
                loads.Add(MetadataReference.CreateFromImage(Resources.netstandard));
                loads.Add(MetadataReference.CreateFromImage(Resources.Microsoft_CSharp));
            }

            var compiler = CSharpCompilation.Create("HMMCodes", trees, loads, options).AddSyntaxTrees(PredefinedClasses);
            
            var result = compiler.Emit(resultStream);
            if (!result.Success)
            {
                var builder = new StringBuilder();
                builder.AppendLine("Error compiling codes");
                foreach (var diagnostic in result.Diagnostics)
                {
                    if (diagnostic.Severity == DiagnosticSeverity.Error)
                    {
                        builder.AppendLine(diagnostic.ToString());
                    }
                }
                throw new Exception(builder.ToString());
            }

            return Task.CompletedTask;
        }
    }

    public static List<MetadataReference> GetLoadAssemblies<TCollection>(TCollection sources, params string[] lookupPaths) where TCollection : IEnumerable<CSharpCode>
    {
        var meta = new List<MetadataReference>();

        var basePath = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var wpfPath = Path.Combine(basePath, "WPF");

        foreach (var source in sources)
        {
            foreach (var load in source.ParseSyntaxTree().PreprocessorDirectives.Where(x => x.Kind == SyntaxTokenKind.LoadDirectiveTrivia))
            {
                var value = load.Value.ToString();
                var path = Path.Combine(basePath, value);
                if (File.Exists(path))
                {
                    meta.Add(MetadataReference.CreateFromFile(path));
                    continue;
                }

                path = Path.Combine(wpfPath, value);
                if (File.Exists(path))
                {
                    meta.Add(MetadataReference.CreateFromFile(path));
                    continue;
                }

                foreach (var lookupPath in lookupPaths)
                {
                    path = Path.Combine(lookupPath, value);
                    if (File.Exists(path))
                    {
                        meta.Add(MetadataReference.CreateFromFile(path));
                        break;
                    }
                }
            }
        }

        return meta;
    }
}

public class OptionalColonRewriter : CSharpSyntaxRewriter
{
    public override SyntaxToken VisitToken(SyntaxToken token)
    {
        if (token.IsKind(SyntaxKind.SemicolonToken) && token.IsMissing)
        {
            return SyntaxFactory.Token(SyntaxKind.SemicolonToken);
        }
        return base.VisitToken(token);
    }
}