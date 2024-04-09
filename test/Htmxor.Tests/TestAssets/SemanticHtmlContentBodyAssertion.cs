﻿using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using AngleSharp;
using AngleSharp.Diffing.Core;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Bunit;
using Bunit.Diffing;
using Bunit.Rendering;
using Microsoft.AspNetCore.Http;

namespace Htmxor.TestAssets;

public sealed class SemanticHtmlContentBodyAssertion : IScenarioAssertion
{
    private static HtmlComparer htmlComparer = new HtmlComparer();

    public string Expected { get; }

    public SemanticHtmlContentBodyAssertion(string expected)
    {
        Expected = expected;
    }

    public void Assert(Scenario scenario, HttpContext context, ScenarioAssertionException ex)
    {
        var received = ex.ReadBody(context);

        using var parser = new BunitHtmlParser();
        IEnumerable<INode> expectedNodes = parser.Parse(Expected);

        if (expectedNodes.FirstOrDefault() is { NodeType: NodeType.DocumentType })
        {
            expectedNodes = expectedNodes.Skip(1);
        }

        IEnumerable<INode> receivedNodes = parser.Parse(received);

        if (receivedNodes.FirstOrDefault() is { NodeType: NodeType.DocumentType })
        {
            receivedNodes = receivedNodes.Skip(1);
        }

        var diffs = htmlComparer.Compare(expectedNodes, receivedNodes).ToArray();

        if (diffs.Length > 0)
        {
            var builder = new StringBuilder();
            builder.AppendLine("Response body does not contain the expected HTML:");
            builder.AppendLine();
            CreateDiffMessage(
                receivedNodes.ToDiffMarkup(),
                expectedNodes.ToDiffMarkup(),
                diffs,
                builder);
            ex.Add(builder.ToString());
        }
    }

    private static void CreateDiffMessage(string received, string verified, IReadOnlyList<IDiff> diffs, StringBuilder builder)
    {
        builder.AppendLine();
        builder.AppendLine("HTML comparison failed. The following errors were found:");

        for (var i = 0; i < diffs.Count; i++)
        {
            builder.Append($"  {i + 1}: ");
            builder.AppendLine(diffs[i] switch
            {
                NodeDiff { Target: DiffTarget.Text } diff when diff.Control.Path.Equals(diff.Test.Path, StringComparison.Ordinal)
                    => $"The text in {diff.Control.Path} is different.",
                NodeDiff { Target: DiffTarget.Text } diff => $"The expected {NodeName(diff.Control)} at {diff.Control.Path} and the actual {NodeName(diff.Test)} at {diff.Test.Path} is different.",
                NodeDiff diff when diff.Control.Path.Equals(diff.Test.Path, StringComparison.Ordinal)
                    => $"The {NodeName(diff.Control)}s at {diff.Control.Path} are different.",
                NodeDiff diff => $"The expected {NodeName(diff.Control)} at {diff.Control.Path} and the actual {NodeName(diff.Test)} at {diff.Test.Path} are different.",
                AttrDiff diff when diff.Control.Path.Equals(diff.Test.Path, StringComparison.Ordinal)
                    => $"The values of the attributes at {diff.Control.Path} are different.",
                AttrDiff diff => $"The value of the attribute {diff.Control.Path} and actual attribute {diff.Test.Path} are different.",
                MissingNodeDiff diff => $"The {NodeName(diff.Control)} at {diff.Control.Path} is missing.",
                MissingAttrDiff diff => $"The attribute at {diff.Control.Path} is missing.",
                UnexpectedNodeDiff diff => $"The {NodeName(diff.Test)} at {diff.Test.Path} was not expected.",
                UnexpectedAttrDiff diff => $"The attribute at {diff.Test.Path} was not expected.",
                _ => throw new SwitchExpressionException($"Unknown diff type detected: {diffs[i].GetType()}")
            });
        }

        builder.AppendLine(
            $"""

             Actual HTML:

             {received}

             Expected HTML:

             {verified}
             """);

        static string NodeName(ComparisonSource source) => source.Node.NodeType.ToString().ToLowerInvariant();
    }
}

/// <summary>
/// A AngleSharp based HTML Parse that can parse markup strings
/// into a <see cref="INodeList"/>.
/// </summary>
public sealed class BunitHtmlParser : IDisposable
{
    private const string TbodySubElements = "TR";
    private const string ColgroupSubElement = "COL";
    private static readonly string[] TableSubElements = { "CAPTION", "COLGROUP", "TBODY", "TFOOT", "THEAD", };
    private static readonly string[] TrSubElements = { "TD", "TH" };
    private static readonly string[] SpecialHtmlElements = { "HTML", "HEAD", "BODY", "!DOCTYPE" };

    private readonly IBrowsingContext context;
    private readonly IHtmlParser htmlParser;
    private readonly List<IDocument> documents = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="BunitHtmlParser"/> class
    /// with a AngleSharp context without a <see cref="TestRenderer"/> registered.
    /// </summary>
    public BunitHtmlParser()
        : this(AngleSharp.Configuration.Default.WithCss().With(new HtmlComparer())) { }

    private BunitHtmlParser(IConfiguration angleSharpConfiguration)
    {
        var config = angleSharpConfiguration.With(this);
        context = BrowsingContext.New(config);
        var parseOptions = new HtmlParserOptions
        {
            IsKeepingSourceReferences = true,
        };
        htmlParser = new HtmlParser(parseOptions, context);
    }

    /// <summary>
    /// Parses a markup HTML string using AngleSharps HTML5 parser.
    /// </summary>
    /// <param name="markup">The markup to parse.</param>
    /// <returns>The <see cref="INodeList"/>.</returns>
    public INodeList Parse([StringSyntax("Html")] string markup)
    {
        if (markup is null)
            throw new ArgumentNullException(nameof(markup));

        var document = GetNewDocumentAsync().GetAwaiter().GetResult();

        var (ctx, matchedElement) = GetParseContext(markup, document);

        return ctx is null && matchedElement is not null
            ? ParseSpecial(markup, matchedElement)
            : htmlParser.ParseFragment(markup, ctx!);
    }

    private INodeList ParseSpecial(string markup, string matchedElement)
    {
        var doc = htmlParser.ParseDocument(markup);

        return matchedElement switch
        {
            "HTML" => new SingleNodeNodeList(doc.Body?.ParentElement),
            "HEAD" => new SingleNodeNodeList(doc.Head),
            "BODY" => new SingleNodeNodeList(doc.Body),
            "!DOCTYPE" => doc.ChildNodes,
            _ => throw new InvalidOperationException($"{matchedElement} should not be parsed by {nameof(ParseSpecial)}."),
        };
    }

    private static (IElement? Context, string? MatchedElement) GetParseContext(
        ReadOnlySpan<char> markup,
        IDocument document)
    {
        var startIndex = markup.IndexOfFirstNonWhitespaceChar();

        // verify that first non-whitespace characters is a '<'
        if (markup.Length > 0 && markup[startIndex].IsTagStart())
        {
            return GetParseContextFromTag(markup, startIndex, document);
        }

        return (Context: document.Body, MatchedElement: null);
    }

    private static (IElement? Context, string? MatchedElement) GetParseContextFromTag(
        ReadOnlySpan<char> markup,
        int startIndex,
        IDocument document)
    {
        Debug.Assert(document.Body is not null, "Body of the document should never be null at this point.");

        IElement? result = null;

        if (markup.StartsWithElements(TableSubElements, startIndex, out var matchedElement))
        {
            result = CreateTable();
        }
        else if (markup.StartsWithElements(TrSubElements, startIndex, out matchedElement))
        {
            result = CreateTable().AppendElement(document.CreateElement("tr"));
        }
        else if (markup.StartsWithElement(TbodySubElements, startIndex))
        {
            result = CreateTable().AppendElement(document.CreateElement("tbody"));
            matchedElement = TbodySubElements;
        }
        else if (markup.StartsWithElement(ColgroupSubElement, startIndex))
        {
            result = CreateTable().AppendElement(document.CreateElement("colgroup"));
            matchedElement = ColgroupSubElement;
        }
        else if (markup.StartsWithElements(SpecialHtmlElements, startIndex, out matchedElement))
        {
            // default case, nothing to do.
        }
        else
        {
            result = document.Body;
        }

        return (Context: result, MatchedElement: matchedElement);

        IElement CreateTable() => document.Body.AppendElement(document.CreateElement("table"));
    }

    private async Task<IDocument> GetNewDocumentAsync()
    {
        var result = await context.OpenNewAsync("about:blank").ConfigureAwait(false);
        documents.Add(result);
        return result;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        context.Dispose();
        foreach (var doc in documents)
        {
            doc.Dispose();
        }
    }

    private sealed class SingleNodeNodeList : INodeList, IReadOnlyList<INode>
    {
        private readonly INode node;

        public INode this[int index]
        {
            get
            {
                if (index != 0)
                    throw new IndexOutOfRangeException();

                return node;
            }
        }

        public int Length => 1;

        public int Count => 1;

        public SingleNodeNodeList(INode? node) => this.node = node ?? throw new ArgumentNullException(nameof(node));

        public IEnumerator<INode> GetEnumerator()
        {
            yield return node;
        }

        public void ToHtml(TextWriter writer, IMarkupFormatter formatter) => node.ToHtml(writer, formatter);

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

internal static class BunitHtmlParserHelpers
{
    internal static bool StartsWithElements(
        this ReadOnlySpan<char> markup,
        string[] tags,
        int startIndex,
        [NotNullWhen(true)] out string? matchedElement)
    {
        matchedElement = null;

        for (int i = 0; i < tags.Length; i++)
        {
            if (markup.StartsWithElement(tags[i], startIndex))
            {
                matchedElement = tags[i];
                return true;
            }
        }

        return false;
    }

    internal static bool StartsWithElement(this ReadOnlySpan<char> markup, string tag, int startIndex)
    {
        var matchesTag = tag.Length + 1 < markup.Length - startIndex;
        var charIndexAfterTag = tag.Length + startIndex + 1;

        if (matchesTag)
        {
            var charAfterTag = markup[charIndexAfterTag];
            matchesTag = char.IsWhiteSpace(charAfterTag) ||
                         charAfterTag == '>' ||
                         charAfterTag == '/';
        }

        // match characters in tag
        for (int i = 0; i < tag.Length && matchesTag; i++)
        {
            matchesTag = char.ToUpperInvariant(markup[startIndex + i + 1]) == tag[i];
        }

        // look for start tags end - '>'
        for (int i = charIndexAfterTag; i < markup.Length && matchesTag; i++)
        {
            if (markup[i] == '>')
                break;
        }

        return matchesTag;
    }

    internal static bool IsTagStart(this char c) => c == '<';

    internal static int IndexOfFirstNonWhitespaceChar(this ReadOnlySpan<char> markup)
    {
        for (int i = 0; i < markup.Length; i++)
        {
            if (!char.IsWhiteSpace(markup[i]))
                return i;
        }

        return 0;
    }
}