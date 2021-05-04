﻿// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Html;
using Pocket;

namespace Microsoft.DotNet.Interactive.Formatting
{
    public class FormatContext : IDisposable
    {
        private Dictionary<string, IHtmlContent> _requiredContent;

        public FormatContext() : this(new StringWriter())
        {
        }

        public FormatContext(TextWriter writer)
        {
            Writer = writer;
        }

        internal int Depth { get; private set; }

        internal int TableDepth { get; private set; }

        public TextWriter Writer { get; }

        internal void Require(string id, IHtmlContent content)
        {
            if (_requiredContent is null)
            {
                _requiredContent = new();
            }

            if (!_requiredContent.ContainsKey(id))
            {
                _requiredContent.Add(id, content);
            }
        }

        internal IDisposable IncrementDepth()
        {
            Depth++;
            return Disposable.Create(() => Depth--);
        }

        internal IDisposable IncrementTableDepth()
        {
            TableDepth++;
            return Disposable.Create(() => TableDepth--);
        }

        public void Dispose()
        {
            if (_requiredContent is not null)
            {
                foreach (var content in _requiredContent.Values)
                {
                    content.WriteTo(Writer, HtmlEncoder.Default);
                }

                _requiredContent = null;
            }
        }
    }
}