﻿// Copyright (c) Microsoft. All rights reserved. 
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.IO;

namespace Microsoft.DotNet.Interactive.Formatting
{
    /// <summary>
    /// Provides formatting functionality for a specific type.
    /// </summary>
    /// <typeparam name="T">The type for which formatting is provided.</typeparam>
    public static class Formatter<T>
    {
        internal static readonly bool TypeIsAnonymous = typeof(T).IsAnonymous();
        internal static readonly bool TypeIsException = typeof(Exception).IsAssignableFrom(typeof(T));
        internal static readonly bool TypeIsTuple = typeof(T).IsTuple();
        internal static readonly bool TypeIsValueTuple = typeof(T).IsValueTuple();

        private static int? _listExpansionLimit;

        /// <summary>
        /// Initializes the <see cref="Formatter&lt;T&gt;"/> class.
        /// </summary>
        static Formatter()
        {
            void Initialize()
            {
                _listExpansionLimit = null;
            }

            Initialize();

            Formatter.Clearing += (o, e) => Initialize();
        }

        /// <summary>
        /// Formats an object and writes it to the specified writer.
        /// </summary>
        /// <param name="obj">The object to be formatted.</param>
        /// <param name="writer">The writer.</param>
        /// <param name="context">The context for the current format operation.</param>
        /// <param name="mimeType">The mime type to format to.</param>
        public static void FormatTo(
            T obj,
            TextWriter writer,
            FormatContext context,
            string mimeType = PlainTextFormatter.MimeType)
        {
            if (obj is null)
            {
                var formatter = Formatter.GetPreferredFormatterFor(typeof(T), mimeType);
                formatter.Format(null, writer, context);
                return;
            }

            using var _ = Formatter.RecursionCounter.Enter();
          
            if (Formatter.RecursionCounter.Depth <= Formatter.RecursionLimit)
            {
                var formatter = Formatter.GetPreferredFormatterFor(typeof(T), mimeType);
                formatter.Format(obj, writer, context);
            }
            else
            {
                PlainTextFormatter<T>.Default.Format(obj, writer, context);
            }
        }

        public static int ListExpansionLimit
        {
            get => _listExpansionLimit ?? Formatter.ListExpansionLimit;
            set => _listExpansionLimit = value;
        }
    }
}