﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.CodeAnalysis.CodeRefactorings;

internal static class CodeRefactoringContextExtensions
{
    /// <summary>
    /// Use this helper to register multiple refactorings (<paramref name="actions"/>).
    /// </summary>
    public static void RegisterRefactorings<TCodeAction>(
        this CodeRefactoringContext context, ImmutableArray<TCodeAction> actions, TextSpan? applicableToSpan = null)
        where TCodeAction : CodeAction
    {
        if (!actions.IsDefault)
        {
            foreach (var action in actions)
            {
                if (applicableToSpan != null)
                {
                    context.RegisterRefactoring(action, applicableToSpan.Value);
                }
                else
                {
                    context.RegisterRefactoring(action);
                }
            }
        }
    }

    public static Task<TSyntaxNode?> TryGetRelevantNodeAsync<TSyntaxNode>(this CodeRefactoringContext context) where TSyntaxNode : SyntaxNode
        => TryGetRelevantNodeAsync<TSyntaxNode>(context, allowEmptyNode: false);

    public static async Task<TSyntaxNode?> TryGetRelevantNodeAsync<TSyntaxNode>(this CodeRefactoringContext context, bool allowEmptyNode) where TSyntaxNode : SyntaxNode
    {
        var parsedDocument = await ParsedDocument.CreateAsync(context.Document, context.CancellationToken).ConfigureAwait(false);
        return TryGetRelevantNode<TSyntaxNode>(parsedDocument, context.Span, allowEmptyNode, context.CancellationToken);
    }

    public static Task<ImmutableArray<TSyntaxNode>> GetRelevantNodesAsync<TSyntaxNode>(this CodeRefactoringContext context) where TSyntaxNode : SyntaxNode
        => GetRelevantNodesAsync<TSyntaxNode>(context, allowEmptyNodes: false);

    public static async Task<ImmutableArray<TSyntaxNode>> GetRelevantNodesAsync<TSyntaxNode>(this CodeRefactoringContext context, bool allowEmptyNodes) where TSyntaxNode : SyntaxNode
    {
        var parsedDocument = await ParsedDocument.CreateAsync(context.Document, context.CancellationToken).ConfigureAwait(false);
        return GetRelevantNodes<TSyntaxNode>(parsedDocument, context.Span, allowEmptyNodes, context.CancellationToken);
    }

    public static TSyntaxNode? TryGetRelevantNode<TSyntaxNode>(this ParsedDocument document, TextSpan span, CancellationToken cancellationToken) where TSyntaxNode : SyntaxNode
        => TryGetRelevantNode<TSyntaxNode>(document, span, allowEmptyNode: false, cancellationToken);

    public static TSyntaxNode? TryGetRelevantNode<TSyntaxNode>(this ParsedDocument document, TextSpan span, bool allowEmptyNode, CancellationToken cancellationToken) where TSyntaxNode : SyntaxNode
    {
        var potentialNodes = GetRelevantNodes<TSyntaxNode>(document, span, allowEmptyNode, cancellationToken);
        return potentialNodes.FirstOrDefault();
    }

    public static ImmutableArray<TSyntaxNode> GetRelevantNodes<TSyntaxNode>(
        this ParsedDocument document, TextSpan span, CancellationToken cancellationToken) where TSyntaxNode : SyntaxNode
        => GetRelevantNodes<TSyntaxNode>(document, span, allowEmptyNodes: false, cancellationToken);

    public static ImmutableArray<TSyntaxNode> GetRelevantNodes<TSyntaxNode>(
        this ParsedDocument document, TextSpan span, bool allowEmptyNodes, CancellationToken cancellationToken) where TSyntaxNode : SyntaxNode
    {
        var helpers = document.LanguageServices.GetRequiredService<IRefactoringHelpersService>();
        return helpers.GetRelevantNodes<TSyntaxNode>(document, span, allowEmptyNodes, cancellationToken);
    }
}
