﻿// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Microsoft.DotNet.Interactive.HttpRequest;

internal class HttpLexer
{
    private TextWindow _textWindow;
    private readonly string _sourceText;
    private readonly HttpSyntaxTree _syntaxTree;
    private readonly List<HttpSyntaxToken> _tokens = new();

    public HttpLexer(string sourceText, HttpSyntaxTree syntaxTree)
    {
        _sourceText = sourceText;
        _syntaxTree = syntaxTree;
    }

    public IReadOnlyList<HttpSyntaxToken> Lex()
    {
        _textWindow = new TextWindow(0, _sourceText.Length);

        HttpTokenKind? previousTokenKind = null;

        while (More())
        {
            var currentCharacter = _sourceText[_textWindow.End];

            var currentTokenKind = currentCharacter switch
            {
                ' ' or '\t' => HttpTokenKind.Whitespace,
                '\n' or '\r' or '\v' => HttpTokenKind.NewLine,
                _ => Char.IsPunctuation(currentCharacter)
                         ? HttpTokenKind.Punctuation
                         : HttpTokenKind.Word,
            };

            if (previousTokenKind is { } previousTokenKindValue &&
                (previousTokenKind != currentTokenKind ||
                 currentTokenKind == HttpTokenKind.Punctuation))
            {
                FlushToken(previousTokenKindValue);
            }

            previousTokenKind = currentTokenKind;

            _textWindow.Advance();
        }

        if (previousTokenKind is not null)
        {
            FlushToken(previousTokenKind.Value);
        }

        return _tokens;
    }

    private bool IsNextCharacterLetter() => Char.IsLetter(GetNextChar());

    [DebuggerHidden]
    private char GetNextChar() => _sourceText[_textWindow.End];

    [DebuggerHidden]
    private char GetPreviousChar() =>
        _textWindow.End switch
        {
            0 => default,
            _ => _sourceText[_textWindow.End - 1]
        };

    private void FlushToken(HttpTokenKind kind)
    {
        if (_textWindow.IsEmpty)
        {
            return;
        }

        _tokens.Add(new HttpSyntaxToken(kind, _sourceText, _textWindow.Span, _syntaxTree));

        _textWindow = new TextWindow(_textWindow.End, _sourceText.Length);
    }

    private bool More()
    {
        return _textWindow.End < _sourceText.Length;
    }
}