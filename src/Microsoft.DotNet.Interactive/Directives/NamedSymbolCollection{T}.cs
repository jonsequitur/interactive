﻿// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;

namespace Microsoft.DotNet.Interactive.Directives;

internal class NamedSymbolCollection<T> : ICollection<T>
{
    private readonly Func<T, string> _getName;
    private readonly Action<T>? _onAdd;
    private readonly List<T> _items = new();
    private readonly Dictionary<string, T> _itemsByName = new();

    public NamedSymbolCollection(Func<T, string> getName, Action<T>? onAdd = null)
    {
        _getName = getName;
        _onAdd = onAdd;
    }

    public bool TryGetValue(string name, out T item)
    {
        return _itemsByName.TryGetValue(name, out item);
    }

    public IEnumerator<T> GetEnumerator()
    {
        return _items.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return _items.GetEnumerator();
    }

    public void Add(T item)
    {
        _itemsByName.Add(_getName(item), item);
        _items.Add(item);
        _onAdd?.Invoke(item);
    }

    public void Clear()
    {
        _items.Clear();
    }

    public bool Contains(T item)
    {
        return _items.Contains(item);
    }

    public void CopyTo(T[] array, int arrayIndex)
    {
        _items.CopyTo(array, arrayIndex);
    }

    public bool Remove(T item)
    {
        return _items.Remove(item);
    }

    public int Count => _items.Count;

    public bool IsReadOnly => false;
}