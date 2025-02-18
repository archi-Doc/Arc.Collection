﻿// Copyright (c) All contributors. All rights reserved. Licensed under the MIT license.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Arc.Collections.HotMethod;

#pragma warning disable SA1124 // Do not use regions
#pragma warning disable SA1202 // Elements should be ordered by access

namespace Arc.Collections;

/// <summary>
/// Represents a collection of objects that is maintained in sorted order (ascending by default).<br/>
/// <see cref="OrderedMultiMap{TKey, TValue}"/> uses Red-Black Tree + Linked List structure to store objects.<br/>
/// <see cref="OrderedMultiMap{TKey, TValue}"/> can store duplicate keys.
/// </summary>
/// <typeparam name="TKey">The type of keys in the collection.</typeparam>
/// <typeparam name="TValue">The type of values in the collection.</typeparam>
public class OrderedMultiMap<TKey, TValue> : IDictionary<TKey, TValue>, IReadOnlyDictionary<TKey, TValue>, IDictionary
{
    #region Node

    /// <summary>
    /// Represents a node in a <see cref="OrderedMultiMap{TKey, TValue}"/>.
    /// SingleNode: Tree node. Color = Black or Red, ListPrevious/ListNext = null.
    /// HeadNode: Tree node and the first node of a linked list. Color = Black or Red, ListPrevious/ListNext != null.
    /// LinkedListNode: Linked list node. Color = LinkedList, ListPrevious/ListNext != null.
    /// </summary>
    public class Node
    {
        internal Node(TKey key, TValue value, NodeColor color)
        {
            this.Key = key;
            this.Value = value;
            this.Color = color;
        }

        /// <summary>
        /// Gets the key contained in the node.
        /// </summary>
        public TKey Key { get; internal set; }

        /// <summary>
        /// Gets the value contained in the node.
        /// </summary>
        public TValue Value { get; internal set; }

        /// <summary>
        /// Gets or sets the parent node in the <see cref="OrderedMultiMap{TKey, TValue}"/>.
        /// </summary>
        internal Node? Parent { get; set; }

        /// <summary>
        /// Gets or sets the left node in the <see cref="OrderedMultiMap{TKey, TValue}"/>.
        /// </summary>
        internal Node? Left { get; set; }

        /// <summary>
        /// Gets or sets the right node in the <see cref="OrderedMultiMap{TKey, TValue}"/>.
        /// </summary>
        internal Node? Right { get; set; }

        /// <summary>
        /// Gets or sets the previous linked list node (doubly-Linked circular list).
        /// </summary>
        internal Node? ListPrevious { get; set; }

        /// <summary>
        /// Gets or sets the next linked list node (doubly-Linked circular list).
        /// </summary>
        internal Node? ListNext { get; set; }

        /// <summary>
        /// Gets or sets the color of the node.
        /// </summary>
        internal NodeColor Color { get; set; }

        /// <summary>
        /// Gets the previous node in the <see cref="OrderedMultiMap{TKey, TValue}"/>.
        /// <br/>O(log n) operation.
        /// </summary>
        public Node? Previous
        {
            get
            {
                if (this.IsLinkedListNode)
                {// LinkedListNode
                    return this.ListPrevious;
                }
                else
                {// SingleNode or HeadNode
                    Node? node;
                    if (this.Left == null)
                    {
                        node = this;
                        Node? p = this.Parent;
                        while (p != null && node == p.Left)
                        {
                            node = p;
                            p = p.Parent;
                        }

                        return p == null ? null : (p.IsSingleNode ? p : p.ListPrevious); // Last node (ListPrevious) if p is a HeadNode.
                    }
                    else
                    {
                        node = this.Left;
                        while (node.Right != null)
                        {
                            node = node.Right;
                        }

                        return node.IsSingleNode ? node : node.ListPrevious; // Last node (ListPrevious) if node is a HeadNode.
                    }
                }
            }
        }

        /// <summary>
        /// Gets the next node in the <see cref="OrderedMultiMap{TKey, TValue}"/>
        /// <br/>O(log n) operation.
        /// </summary>
        public Node? Next
        {
            get
            {
                Node treeNode;
                if (this.IsSingleNode)
                {// SingleNode
                    treeNode = this;
                }
                else if (this.IsLinkedListNode)
                {// LinkedListNode
                    if (this.ListNext!.IsLinkedListNode)
                    {// Next LinkedListNode
                        return this.ListNext;
                    }
                    else
                    {// HeadNode -> Next tree node
                        treeNode = this.ListNext;
                    }
                }
                else
                {// HeadNode
                    return this.ListNext;
                }

                Node? node;
                if (treeNode.Right == null)
                {
                    node = treeNode;
                    Node? p = treeNode.Parent;
                    while (p != null && node == p.Right)
                    {
                        node = p;
                        p = p.Parent;
                    }

                    return p;
                }
                else
                {
                    node = treeNode.Right;
                    while (node.Left != null)
                    {
                        node = node.Left;
                    }

                    return node;
                }
            }
        }

        public void UnsafeChangeValue(TValue value) => this.Value = value;

        internal static bool IsNonNullBlack(Node? node) => node != null && node.IsBlack;

        internal static bool IsNonNullRed(Node? node) => node != null && node.IsRed;

        internal static bool IsNullOrBlack(Node? node) => node == null || node.IsBlack;

        internal bool IsBlack => this.Color == NodeColor.Black;

        internal bool IsRed => this.Color == NodeColor.Red;

        internal bool IsUnused => this.Color == NodeColor.Unused;

        internal bool IsLinkedListNode => this.Color == NodeColor.LinkedList;

        internal bool IsSingleNode => this.ListPrevious == null;

        public override string ToString() => this.Color.ToString() + ": " + this.Value?.ToString();

        internal void ColorBlack() => this.Color = NodeColor.Black;

        internal void ColorRed() => this.Color = NodeColor.Red;

        internal void Clear()
        {
            this.Key = default(TKey)!;
            this.Value = default(TValue)!;
            this.Parent = null;
            this.Left = null;
            this.Right = null;
            this.ListPrevious = null;
            this.ListNext = null;
            this.Color = NodeColor.Unused;
        }

        internal void Reset(TKey key, TValue value, NodeColor color)
        {
            this.Key = key;
            this.Value = value;
            this.Parent = null;
            this.Left = null;
            this.Right = null;
            this.ListPrevious = null;
            this.ListNext = null;
            this.Color = color;
        }
    }

    #endregion

    private Node? root;
    private int version;
    private KeyCollection? keys;
    private ValueCollection? values;

    /// <summary>
    /// Gets the number of nodes actually contained in the <see cref="OrderedMultiMap{TKey, TValue}"/>.
    /// </summary>
    public int Count { get; private set; }

    public int CompareFactor { get; }

    public IComparer<TKey> Comparer { get; private set; }

    public IHotMethod2<TKey, TValue>? HotMethod2 { get; private set; }

    // public bool UnsafePresearchForStructKey { get; set; } = false;

    /// <summary>
    /// Initializes a new instance of the <see cref="OrderedMultiMap{TKey, TValue}"/> class.
    /// </summary>
    /// <param name="reverse">true to reverses the comparison provided by the comparer. </param>
    public OrderedMultiMap(bool reverse = false)
    {
        this.CompareFactor = reverse ? -1 : 1;
        this.Comparer = Comparer<TKey>.Default;
        this.HotMethod2 = HotMethodResolver.Get<TKey, TValue>(this.Comparer);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="OrderedMultiMap{TKey, TValue}"/> class.
    /// </summary>
    /// <param name="comparer">The default comparer to use for comparing objects.</param>
    /// <param name="reverse">true to reverses the comparison provided by the comparer. </param>
    public OrderedMultiMap(IComparer<TKey> comparer, bool reverse = false)
    {
        this.CompareFactor = reverse ? -1 : 1;
        this.Comparer = comparer ?? Comparer<TKey>.Default;
        this.HotMethod2 = HotMethodResolver.Get<TKey, TValue>(this.Comparer);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="OrderedMultiMap{TKey, TValue}"/> class.
    /// </summary>
    /// <param name="dictionary">The IDictionary implementation to copy to a new collection.</param>
    /// <param name="reverse">true to reverses the comparison provided by the comparer. </param>
    public OrderedMultiMap(IDictionary<TKey, TValue> dictionary, bool reverse = false)
        : this(dictionary, Comparer<TKey>.Default, reverse)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="OrderedMultiMap{TKey, TValue}"/> class.
    /// </summary>
    /// <param name="dictionary">The IDictionary implementation to copy to a new collection.</param>
    /// <param name="comparer">The default comparer to use for comparing objects.</param>
    /// <param name="reverse">true to reverses the comparison provided by the comparer. </param>
    public OrderedMultiMap(IDictionary<TKey, TValue> dictionary, IComparer<TKey> comparer, bool reverse = false)
    {
        this.CompareFactor = reverse ? -1 : 1;
        this.Comparer = comparer ?? Comparer<TKey>.Default;
        this.HotMethod2 = HotMethodResolver.Get<TKey, TValue>(this.Comparer);

        foreach (var x in dictionary)
        {
            this.Add(x.Key, x.Value);
        }
    }

    /// <summary>
    /// Gets the first node in the <see cref="OrderedMultiMap{TKey, TValue}"/>.
    /// <br/>O(log n) operation.
    /// </summary>
    public Node? First
    {
        get
        {
            if (this.root == null)
            {
                return null;
            }

            var node = this.root;
            while (node.Left != null)
            {
                node = node.Left;
            }

            return node;
        }
    }

    /// <summary>
    /// Gets the last node in the <see cref="OrderedMultiMap{TKey, TValue}"/>.
    /// <br/>O(log n) operation.
    /// </summary>
    public Node? Last
    {
        get
        {
            if (this.root == null)
            {
                return null;
            }

            var node = this.root;
            while (node.Right != null)
            {
                node = node.Right;
            }

            return node.IsSingleNode ? node : node.ListPrevious; // Last node (ListPrevious) if node is a HeadNode.
        }
    }

    #region Enumerator

    public Enumerator GetEnumerator() => new Enumerator(this, Enumerator.KeyValuePair);

    IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator() => new Enumerator(this, Enumerator.KeyValuePair);

    IEnumerator IEnumerable.GetEnumerator() => new Enumerator(this, Enumerator.KeyValuePair);

    public struct Enumerator : IEnumerator<KeyValuePair<TKey, TValue>>, IDictionaryEnumerator
    {
        internal const int KeyValuePair = 1;
        internal const int DictEntry = 2;

        private readonly OrderedMultiMap<TKey, TValue> map;
        private readonly int version;
        private readonly int getEnumeratorRetType;
        private Node? node;
        private TKey? key;
        private TValue? value;

        internal Enumerator(OrderedMultiMap<TKey, TValue> set, int getEnumeratorRetType)
        {
            this.map = set;
            this.version = this.map.version;
            this.getEnumeratorRetType = getEnumeratorRetType;
            this.node = this.map.First;
            this.key = default;
            this.value = default;
        }

        public void Dispose()
        {
            this.node = null;
            this.key = default;
            this.value = default;
        }

        public bool MoveNext()
        {
            if (this.version != this.map.version)
            {
                throw ThrowVersionMismatch();
            }

            if (this.node == null)
            {
                this.key = default(TKey)!;
                this.value = default(TValue)!;
                return false;
            }

            this.key = this.node.Key;
            this.value = this.node.Value;
            this.node = this.node.Next;
            return true;
        }

        DictionaryEntry IDictionaryEnumerator.Entry => new DictionaryEntry(this.key!, this.value!);

        object IDictionaryEnumerator.Key => this.key!;

        object IDictionaryEnumerator.Value => this.value!;

        public KeyValuePair<TKey, TValue> Current => new KeyValuePair<TKey, TValue>(this.key!, this.value!);

        object? IEnumerator.Current
        {
            get
            {
                if (this.getEnumeratorRetType == DictEntry)
                {
                    return new DictionaryEntry(this.key!, this.value!);
                }
                else
                {
                    return new KeyValuePair<TKey, TValue>(this.key!, this.value!);
                }
            }
        }

        void System.Collections.IEnumerator.Reset() => this.Reset();

        internal void Reset()
        {
            if (this.version != this.map.version)
            {
                throw ThrowVersionMismatch();
            }

            this.node = this.map.First;
            this.key = default;
            this.value = default;
        }

        private static Exception ThrowVersionMismatch()
        {
            throw new InvalidOperationException("Collection was modified after the enumerator was instantiated.'");
        }
    }

    #endregion

    #region ICollection

    bool ICollection.IsSynchronized => false;

    object ICollection.SyncRoot => this;

    void ICollection.CopyTo(Array array, int index)
    {
        if (array == null)
        {
            throw new ArgumentNullException(nameof(array));
        }

        if (array.Rank != 1)
        {
            throw new ArgumentException(nameof(array));
        }

        if (array.GetLowerBound(0) != 0)
        {
            throw new ArgumentException(nameof(array));
        }

        if (index < 0 || index > array.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        if (array.Length - index < this.Count)
        {
            throw new ArgumentException();
        }

        var node = this.First;
        KeyValuePair<TKey, TValue>[]? keyValuePairArray = array as KeyValuePair<TKey, TValue>[];
        if (keyValuePairArray != null)
        {
            for (int i = 0; i < this.Count; i++)
            {
                keyValuePairArray[i + index] = new KeyValuePair<TKey, TValue>(node!.Key, node!.Value);
                node = node.Next;
            }
        }
        else
        {
            object[]? objects = array as object[];
            if (objects == null)
            {
                throw new ArgumentException(nameof(array));
            }

            try
            {
                for (int i = 0; i < this.Count; i++)
                {
                    objects[i + index] = new KeyValuePair<TKey, TValue>(node!.Key, node!.Value);
                    node = node.Next;
                }
            }
            catch (ArrayTypeMismatchException)
            {
                throw new ArgumentException(nameof(array));
            }
        }
    }

    #endregion

    #region IDictionary

    object? IDictionary.this[object key]
    {
        get
        {
            if (key == null)
            {
                if (this.TryGetValue(default, out var value))
                {
                    return value!;
                }
            }
            else if (key is TKey k)
            {
                if (this.TryGetValue(k, out var value))
                {
                    return value!;
                }
            }

            return null!;
        }

        set
        {
            this[(TKey)key] = (TValue)value!;
        }
    }

    bool IDictionary.IsFixedSize => false;

    bool IDictionary.IsReadOnly => false;

    ICollection IDictionary.Keys => (ICollection)this.Keys;

    ICollection IDictionary.Values => (ICollection)this.Values;

    void IDictionary.Add(object key, object? value) => this.Add((TKey)key, (TValue)value!);

    bool IDictionary.Contains(object key)
    {
        if (key == null)
        {
            return this.ContainsKey(default);
        }
        else if (key is TKey k)
        {
            return this.ContainsKey(k);
        }

        return false;
    }

    IDictionaryEnumerator IDictionary.GetEnumerator() => new Enumerator(this, Enumerator.DictEntry);

    void IDictionary.Remove(object key)
    {
        if (key == null)
        {
            this.Remove(default);
        }
        else if (key is TKey k)
        {
            this.Remove(k);
        }
    }

    #endregion

    #region IDictionary<TKey, TValue>

    IEnumerable<TKey> IReadOnlyDictionary<TKey, TValue>.Keys => this.Keys;

    IEnumerable<TValue> IReadOnlyDictionary<TKey, TValue>.Values => this.Values;

    ICollection<TKey> IDictionary<TKey, TValue>.Keys => this.Keys;

    ICollection<TValue> IDictionary<TKey, TValue>.Values => this.Values;

    void IDictionary<TKey, TValue>.Add(TKey key, TValue value) => this.Add(key, value);

    #endregion

    #region ICollection<KeyValuePair<TKey,TValue>>

    bool ICollection<KeyValuePair<TKey, TValue>>.IsReadOnly => false;

    void ICollection<KeyValuePair<TKey, TValue>>.Add(KeyValuePair<TKey, TValue> item) => this.Add(item.Key, item.Value);

    bool ICollection<KeyValuePair<TKey, TValue>>.Contains(KeyValuePair<TKey, TValue> item) => this.FindNode(item.Key, item.Value) != null;

    void ICollection<KeyValuePair<TKey, TValue>>.CopyTo(KeyValuePair<TKey, TValue>[] array, int index) => ((ICollection)this).CopyTo(array, index);

    bool ICollection<KeyValuePair<TKey, TValue>>.Remove(KeyValuePair<TKey, TValue> item) => this.Remove(item.Key, item.Value);

    #endregion

    #region KeyValueCollection

    public KeyCollection Keys => this.keys != null ? this.keys : (this.keys = new KeyCollection(this));

    public ValueCollection Values => this.values != null ? this.values : (this.values = new ValueCollection(this));

    public sealed class KeyCollection : ICollection<TKey>, ICollection, IReadOnlyCollection<TKey>
    {
        private readonly OrderedMultiMap<TKey, TValue> map;

        public KeyCollection(OrderedMultiMap<TKey, TValue> map)
        {
            if (map == null)
            {
                throw new ArgumentNullException(nameof(map));
            }

            this.map = map;
        }

        public Enumerator GetEnumerator() => new Enumerator(this.map);

        IEnumerator<TKey> IEnumerable<TKey>.GetEnumerator() => new Enumerator(this.map);

        IEnumerator IEnumerable.GetEnumerator() => new Enumerator(this.map);

        public void CopyTo(TKey[] array, int index)
        {
            if (array == null)
            {
                throw new ArgumentNullException(nameof(array));
            }

            if (index < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            if (array.Length - index < this.Count)
            {
                throw new ArgumentException();
            }

            var node = this.map.First;
            while (node != null)
            {
                array[index++] = node.Key;
                node = node.Next;
            }
        }

        void ICollection.CopyTo(Array array, int index)
        {
            if (array == null)
            {
                throw new ArgumentNullException(nameof(array));
            }

            if (array.Rank != 1)
            {
                throw new ArgumentException(nameof(array));
            }

            if (array.GetLowerBound(0) != 0)
            {
                throw new ArgumentException(nameof(array));
            }

            if (index < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            if (array.Length - index < this.map.Count)
            {
                throw new ArgumentException();
            }

            TKey[]? keys = array as TKey[];
            if (keys != null)
            {
                this.CopyTo(keys, index);
            }
            else
            {
                try
                {
                    object[] objects = (object[])array;
                    var node = this.map.First;
                    while (node != null)
                    {
                        objects[index++] = node.Key!;
                        node = node.Next;
                    }
                }
                catch (ArrayTypeMismatchException)
                {
                    throw new ArgumentException(nameof(array));
                }
            }
        }

        public int Count => this.map.Count;

        bool ICollection<TKey>.IsReadOnly => true;

        void ICollection<TKey>.Add(TKey item) => throw new NotSupportedException();

        void ICollection<TKey>.Clear() => throw new NotSupportedException();

        bool ICollection<TKey>.Contains(TKey item) => this.map.ContainsKey(item);

        bool ICollection<TKey>.Remove(TKey item) => throw new NotSupportedException();

        bool ICollection.IsSynchronized => false;

        object ICollection.SyncRoot => ((ICollection)this.map).SyncRoot;

        public struct Enumerator : IEnumerator<TKey>, IEnumerator
        {
            private IEnumerator<KeyValuePair<TKey, TValue>> mapEnum;

            internal Enumerator(OrderedMultiMap<TKey, TValue> map)
            {
                this.mapEnum = map.GetEnumerator();
            }

            public void Dispose() => this.mapEnum.Dispose();

            public bool MoveNext() => this.mapEnum.MoveNext();

            public TKey Current => this.mapEnum.Current.Key;

            object? IEnumerator.Current => this.Current;

            void IEnumerator.Reset() => this.mapEnum.Reset();
        }
    }

    public sealed class ValueCollection : ICollection<TValue>, ICollection, IReadOnlyCollection<TValue>
    {
        private readonly OrderedMultiMap<TKey, TValue> map;

        public ValueCollection(OrderedMultiMap<TKey, TValue> map)
        {
            if (map == null)
            {
                throw new ArgumentNullException(nameof(map));
            }

            this.map = map;
        }

        public Enumerator GetEnumerator() => new Enumerator(this.map);

        IEnumerator<TValue> IEnumerable<TValue>.GetEnumerator() => new Enumerator(this.map);

        IEnumerator IEnumerable.GetEnumerator() => new Enumerator(this.map);

        public void CopyTo(TValue[] array, int index)
        {
            if (array == null)
            {
                throw new ArgumentNullException(nameof(array));
            }

            if (index < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            if (array.Length - index < this.Count)
            {
                throw new ArgumentException();
            }

            var node = this.map.First;
            while (node != null)
            {
                array[index++] = node.Value;
                node = node.Next;
            }
        }

        void ICollection.CopyTo(Array array, int index)
        {
            if (array == null)
            {
                throw new ArgumentNullException(nameof(array));
            }

            if (array.Rank != 1)
            {
                throw new ArgumentException(nameof(array));
            }

            if (array.GetLowerBound(0) != 0)
            {
                throw new ArgumentException(nameof(array));
            }

            if (index < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            if (array.Length - index < this.map.Count)
            {
                throw new ArgumentException();
            }

            TValue[]? values = array as TValue[];
            if (values != null)
            {
                this.CopyTo(values, index);
            }
            else
            {
                try
                {
                    object?[] objects = (object?[])array;
                    var node = this.map.First;
                    while (node != null)
                    {
                        objects[index++] = node.Value;
                        node = node.Next;
                    }
                }
                catch (ArrayTypeMismatchException)
                {
                    throw new ArgumentException(nameof(array));
                }
            }
        }

        public int Count => this.map.Count;

        bool ICollection<TValue>.IsReadOnly => true;

        void ICollection<TValue>.Add(TValue item) => throw new NotSupportedException();

        void ICollection<TValue>.Clear() => throw new NotSupportedException();

        bool ICollection<TValue>.Contains(TValue item)
        {
            return this.map.ContainsValue(item);
        }

        bool ICollection<TValue>.Remove(TValue item) => throw new NotSupportedException();

        bool ICollection.IsSynchronized => false;

        object ICollection.SyncRoot => ((ICollection)this.map).SyncRoot;

        public struct Enumerator : IEnumerator<TValue>, IEnumerator
        {
            private IEnumerator<KeyValuePair<TKey, TValue>> mapEnum;

            internal Enumerator(OrderedMultiMap<TKey, TValue> map)
            {
                this.mapEnum = map.GetEnumerator();
            }

            public void Dispose() => this.mapEnum.Dispose();

            public bool MoveNext() => this.mapEnum.MoveNext();

            public TValue Current => this.mapEnum.Current.Value;

            object? IEnumerator.Current => this.Current;

            void IEnumerator.Reset() => this.mapEnum.Reset();
        }
    }

    #endregion

    #region Main

    public TValue this[TKey key]
    {
        get
        {
            var node = this.FindFirstNode(key);
            if (node == null)
            {
                throw new KeyNotFoundException();
            }

            return node.Value;
        }

        set
        {
            this.Add(key, value);
        }
    }

    public bool ContainsKey(TKey? key) => this.FindFirstNode(key) != null;

    public bool ContainsValue(TValue value)
    {
        var found = false;

        if (value == null)
        {
            var node = this.First;
            while (node != null)
            {
                if (node.Value == null)
                {
                    found = true;
                    break;
                }

                node = node.Next;
            }
        }
        else
        {
            var comparer = EqualityComparer<TValue>.Default;
            var node = this.First;
            while (node != null)
            {
                if (comparer.Equals(node.Value, value))
                {
                    found = true;
                    break;
                }

                node = node.Next;
            }
        }

        return found;
    }

    public bool TryGetValue(TKey? key, [MaybeNullWhen(false)] out TValue value)
    {
        if (key == null)
        {
            throw new ArgumentNullException(nameof(key));
        }

        var node = this.FindFirstNode(key);
        if (node == null)
        {
            value = default;
            return false;
        }

        value = node.Value;
        return true;
    }

    /// <summary>
    /// Removes all elements from a collection.
    /// </summary>
    public void Clear()
    {
        this.root = null;
        this.version = 0;
        this.Count = 0;
    }

    /// <summary>
    /// Copies the elements of the collection to the specified array of KeyValuePair structures, starting at the specified index.
    /// </summary>
    /// <param name="array">The one-dimensional array of KeyValuePair structures that is the destination of the elements.</param>
    /// <param name="index">The zero-based index in array at which copying begins.</param>
    public void CopyTo(KeyValuePair<TKey, TValue>[] array, int index) => ((ICollection)this).CopyTo(array, index);

    /// <summary>
    /// Removes the first element with the specified key from a collection.
    /// <br/>O(log n) operation.
    /// </summary>
    /// <param name="key">The key of the element to remove.</param>
    /// <returns>true if the element is found and successfully removed.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Remove(TKey? key)
    {
        var p = this.FindFirstNode(key);
        if (p == null)
        {
            return false;
        }

        this.RemoveNode(p);
        return true;
    }

    /// <summary>
    /// Removes the first element with the specified key/value from a collection.
    /// <br/>O(log n) operation (worst case O(n)).
    /// </summary>
    /// <param name="key">The key of the element to remove.</param>
    /// <param name="value">The value of the element to remove.</param>
    /// <returns>true if the element is found and successfully removed.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Remove(TKey key, TValue value)
    {
        var p = this.FindNode(key, value);
        if (p == null)
        {
            return false;
        }

        this.RemoveNode(p);
        return true;
    }

    /// <summary>
    /// Adds an element to a collection. If the element is already in the set, this method returns the stored element without creating a new node, and sets NewlyAdded to false.
    /// <br/>O(log n) operation.
    /// </summary>
    /// <param name="key">The key of the element to add.</param>
    /// <param name="value">The value of the element to add.</param>
    /// <returns>Node: the added <see cref="OrderedMultiMap{TKey, TValue}.Node"/>.<br/>
    /// NewlyAdded:true if the new key is inserted.</returns>
    /// <remarks>To optimize Value creation, we considered using a Factory delegate but decided against it due to performance degradation.<br/>
    /// Instead, consider searching with ContainsKey() or FindNode() first, and if the item does not exist, add the Value using Add().</remarks>
    public (Node Node, bool NewlyAdded) Add(TKey key, TValue value) => this.Probe(key, value, null);

    /// <summary>
    /// Adds an element to a collection. If the element is already in the set, this method returns the stored element without creating a new node, and sets NewlyAdded to false.
    /// <br/>O(log n) operation.
    /// </summary>
    /// <param name="key">The key of the element to add.</param>
    /// <param name="value">The value of the element to add.</param>
    /// <param name="reuse">Reuse a node to avoid memory allocation.</param>
    /// <returns>Node: the added <see cref="OrderedMultiMap{TKey, TValue}.Node"/>.<br/>
    /// NewlyAdded: true if the new key is inserted.</returns>
    /// <remarks>To optimize Value creation, we considered using a Factory delegate but decided against it due to performance degradation.<br/>
    /// Instead, consider searching with ContainsKey() or FindNode() first, and if the item does not exist, add the Value using Add().</remarks>
    public (Node Node, bool NewlyAdded) Add(TKey key, TValue value, Node reuse) => this.Probe(key, value, reuse);

    /// <summary>
    /// Updates the node's key with the specified key. Removes the node and inserts in the correct position if necessary.
    /// <br/>O(log n) operation.
    /// </summary>
    /// <param name="node">The <see cref="OrderedMap{TKey, TValue}.Node"/> to change the key.</param>
    /// <param name="key">The key to set.</param>
    /// <returns>true if the key is changed.</returns>
    public bool SetNodeKey(Node node, TKey key)
    {
        var cmp = this.Comparer.Compare(node.Key, key);
        if (cmp == 0)
        {// Identical
            return false;
        }
        else if (this.CompareFactor > 0)
        {
            if (cmp < 0)
            {// node.Key < key
                if (node.Next is null || this.Comparer.Compare(node.Next.Key, key) > 0)
                {// node.Next.Key > key
                    node.Key = key;
                    return true;
                }
            }
            else
            {// node.Key > key
                if (node.Previous is null || this.Comparer.Compare(node.Previous.Key, key) < 0)
                {// node.Previous.Key < key
                    node.Key = key;
                    return true;
                }
            }
        }
        else
        {
            if (cmp < 0)
            {// node.Key < key
                if (node.Previous is null || this.Comparer.Compare(node.Previous.Key, key) > 0)
                {// node.Previous.Key > key
                    node.Key = key;
                    return true;
                }
            }
            else
            {// node.Key > key
                if (node.Next is null || this.Comparer.Compare(node.Next.Key, key) < 0)
                {// node.Next.Key < key
                    node.Key = key;
                    return true;
                }
            }
        }

        var value = node.Value;
        this.RemoveNode(node);
        this.Probe(key, value, node);
        return true;
    }

    /// <summary>
    /// Updates the node's value with the specified value.
    /// <br/>O(1) operation.
    /// </summary>
    /// <param name="node">The <see cref="OrderedMap{TKey, TValue}.Node"/> to change the value.</param>
    /// <param name="value">The value to set.</param>
    public void SetNodeValue(Node node, TValue value) => node.Value = value;

    /// <summary>
    /// Removes a specified node from the collection.
    /// <br/>O(log n) operation.
    /// </summary>
    /// <param name="node">The <see cref="OrderedMultiMap{TKey, TValue}.Node"/> to remove.</param>
    public void RemoveNode(Node node)
    {
        Node? f; // Node to fix.
        int dir = 0;

        var originalColor = node.Color;
        if (node.Color == NodeColor.Unused)
        {// empty
            return;
        }

        this.version++;
        this.Count--;

        if (node.Color == NodeColor.LinkedList)
        {// LinkedListNode
            node.ListPrevious!.ListNext = node.ListNext;
            node.ListNext!.ListPrevious = node.ListPrevious;

            var headNode = node.ListNext;
            if (headNode == headNode.ListNext)
            {// HeadNode to SingleNode
                Debug.Assert(!headNode.IsLinkedListNode, "The last node must be a HeadNode.");
                headNode.ListPrevious = null;
                headNode.ListNext = null;
            }

            node.Clear();
            return;
        }
        else if (!node.IsSingleNode)
        {// HeadNode
            node.ListPrevious!.ListNext = node.ListNext;
            node.ListNext!.ListPrevious = node.ListPrevious;

            // LinkedListNode to HeadNode
            var listNode = node.ListNext;
            listNode.Color = node.Color;
            this.TransplantNode(listNode, node);
            listNode.Left = node.Left;
            if (listNode.Left != null)
            {
                listNode.Left.Parent = listNode;
            }

            listNode.Right = node.Right;
            if (listNode.Right != null)
            {
                listNode.Right.Parent = listNode;
            }

            if (listNode.ListNext == listNode)
            {// HeadNode to SingleNode
                Debug.Assert(!listNode.IsLinkedListNode, "The last node must be a HeadNode.");
                listNode.ListPrevious = null;
                listNode.ListNext = null;
            }

            node.Clear();
            return;
        }

        // SingleNode
        f = node.Parent;
        if (node.Parent == null)
        {
            dir = 0;
        }
        else if (node.Parent.Left == node)
        {
            dir = -1;
        }
        else if (node.Parent.Right == node)
        {
            dir = 1;
        }

        if (node.Left == null)
        {
            this.TransplantNode(node.Right, node);
        }
        else if (node.Right == null)
        {
            this.TransplantNode(node.Left, node);
        }
        else
        {
            // Minimum
            Node? m = node.Right;
            while (m.Left != null)
            {
                m = m.Left;
            }

            originalColor = m.Color;
            if (m.Parent == node)
            {
                f = m;
                dir = 1;
            }
            else
            {
                f = m.Parent;
                dir = -1;

                this.TransplantNode(m.Right, m);
                m.Right = node.Right;
                m.Right.Parent = m;
            }

            this.TransplantNode(m, node);
            m.Left = node.Left;
            m.Left.Parent = m;
            m.Color = node.Color;
        }

        if (originalColor == NodeColor.Red || f == null)
        {
            node.Clear();
            if (this.root != null)
            {
                this.root.ColorBlack();
            }

            return;
        }

        Node? s;
        while (true)
        {
            if (dir < 0)
            {
                s = f.Right;
                if (Node.IsNonNullRed(s))
                {
                    s!.ColorBlack();
                    f.ColorRed();
                    this.RotateLeft(f);
                    s = f.Right;
                }

                // s is null or black
                if (s == null)
                {
                    // loop
                }
                else if (Node.IsNullOrBlack(s.Left) && Node.IsNullOrBlack(s.Right))
                {
                    s.ColorRed();
                    // loop
                }
                else
                {// s is black and one of children is red.
                    if (Node.IsNonNullRed(s.Left))
                    {
                        s.Left!.ColorBlack();
                        s.ColorRed();
                        this.RotateRight(s);
                        s = f.Right;
                    }

                    s!.Color = f.Color;
                    f.ColorBlack();
                    s.Right!.ColorBlack();
                    this.RotateLeft(f);
                    break;
                }
            }
            else
            {
                s = f.Left;
                if (Node.IsNonNullRed(s))
                {
                    s!.ColorBlack();
                    f.ColorRed();
                    this.RotateRight(f);
                    s = f.Left;
                }

                // s is null or black
                if (s == null)
                {
                    // loop
                }
                else if (Node.IsNullOrBlack(s.Left) && Node.IsNullOrBlack(s.Right))
                {
                    s.ColorRed();
                    // loop
                }
                else
                {// s is black and one of children is red.
                    if (Node.IsNonNullRed(s.Right))
                    {
                        s.Right!.ColorBlack();
                        s.ColorRed();
                        this.RotateLeft(s);
                        s = f.Left;
                    }

                    s!.Color = f.Color;
                    f.ColorBlack();
                    s.Left!.ColorBlack();
                    this.RotateRight(f);
                    break;
                }
            }

            if (f.IsRed || f.Parent == null)
            {
                f.ColorBlack();
                break;
            }

            if (f == f.Parent.Left)
            {
                dir = -1;
            }
            else
            {
                dir = 1;
            }

            f = f.Parent;
        }

        node.Clear();
        return;
    }

    /*private (int Cmp, Node? P) UnsafePresearch(ref Node? x, TKey key)
    {
        Node? p = default;
        int cmp = 0;

        var k1 = Unsafe.As<TKey, int>(ref key);
        while (x != null)
        {
            p = x;
            var xkey = x.Key;
            var k2 = Unsafe.As<TKey, int>(ref xkey);
            if (k1 < k2)
            {
                cmp = -1;
                x = x.Left;
            }
            else if (k1 > k2)
            {
                cmp = 1;
                x = x.Right;
            }
            else
            {
                return (0, x);
            }
        }

        return (cmp, p);
    }

    private (int Cmp, Node? P) UnsafePresearch2(ref Node? x, TKey key)
    {
        Node? p = default;
        int cmp = 0;

        var k1 = Unsafe.As<TKey, int>(ref key);
        while (x != null)
        {
            p = x;
            var xkey = x.Key;
            var k2 = Unsafe.As<TKey, int>(ref xkey);
            if (k1 > k2)
            {
                cmp = -1;
                x = x.Left;
            }
            else if (k1 < k2)
            {
                cmp = 1;
                x = x.Right;
            }
            else
            {
                return (0, x);
            }
        }

        return (cmp, p);
    }*/

    /// <summary>
    /// Searches a tree for the first node with the specific value.
    /// </summary>
    /// <param name="target">The node to search.</param>
    /// <param name="key">The value to search for.</param>
    /// <returns>cmp: -1 => left, 0 and leaf is not null => found, 1 => right.
    /// leaf: the node with the specific value if found, or the nearest parent node if not found.</returns>
    private (int Cmp, Node? Leaf) SearchFirstNode(Node? target, TKey? key)
    {
        Node? x = target;
        Node? p = null;
        int cmp = 0;

        if (this.CompareFactor > 0)
        {
            if (this.HotMethod2 != null)
            {// HotMethod is available for value type (key is not null).
                return this.HotMethod2.SearchNode(x, key!);
            }
            else if (key == null)
            {// key is null
                while (x != null)
                {
                    if (x.Key == null)
                    {// null == null
                        return (0, x);
                    }
                    else
                    {// null < not null
                        p = x;
                        cmp = -1;
                        x = x.Left;
                    }
                }
            }
            else if (this.Comparer == Comparer<TKey>.Default && key is IComparable<TKey> ic)
            {// IComparable<TKey>
                /*if (this.UnsafePresearchForStructKey)
                {
                    (cmp, p) = this.UnsafePresearch(ref x, key!);
                }*/

                while (x != null)
                {
                    cmp = ic.CompareTo(x.Key); // -1: 1st < 2nd, 0: equals, 1: 1st > 2nd
                    p = x;
                    if (cmp < 0)
                    {
                        x = x.Left;
                    }
                    else if (cmp > 0)
                    {
                        x = x.Right;
                    }
                    else
                    {// Found
                        return (0, x);
                    }
                }
            }
            else
            {// IComparer<TKey>
                /*if (this.UnsafePresearchForStructKey)
                {
                    (cmp, p) = this.UnsafePresearch(ref x, key!);
                }*/

                while (x != null)
                {
                    cmp = this.Comparer.Compare(key, x.Key); // -1: 1st < 2nd, 0: equals, 1: 1st > 2nd
                    p = x;
                    if (cmp < 0)
                    {
                        x = x.Left;
                    }
                    else if (cmp > 0)
                    {
                        x = x.Right;
                    }
                    else
                    {// Found
                        return (0, x);
                    }
                }
            }
        }
        else
        {// Reverse
            if (this.HotMethod2 != null)
            {// HotMethod is available for value type (key is not null).
                return this.HotMethod2.SearchNodeReverse(x, key!);
            }
            else if (key == null)
            {// key is null
                while (x != null)
                {
                    if (x.Key == null)
                    {// null == null
                        return (0, x);
                    }
                    else
                    {// null > not null
                        p = x;
                        cmp = 1;
                        x = x.Right;
                    }
                }
            }
            else if (this.Comparer == Comparer<TKey>.Default && key is IComparable<TKey> ic)
            {// IComparable<TKey>
                /*if (this.UnsafePresearchForStructKey)
                {
                    (cmp, p) = this.UnsafePresearch2(ref x, key!);
                }*/

                while (x != null)
                {
                    cmp = ic.CompareTo(x.Key); // -1: 1st < 2nd, 0: equals, 1: 1st > 2nd
                    p = x;
                    if (cmp > 0)
                    {
                        cmp = -1;
                        x = x.Left;
                    }
                    else if (cmp < 0)
                    {
                        cmp = 1;
                        x = x.Right;
                    }
                    else
                    {// Found
                        return (0, x);
                    }
                }
            }
            else
            {// IComparer<TKey>
                /*if (this.UnsafePresearchForStructKey)
                {
                    (cmp, p) = this.UnsafePresearch2(ref x, key!);
                }*/

                while (x != null)
                {
                    cmp = this.Comparer.Compare(key, x.Key); // -1: 1st < 2nd, 0: equals, 1: 1st > 2nd
                    p = x;
                    if (cmp > 0)
                    {
                        cmp = -1;
                        x = x.Left;
                    }
                    else if (cmp < 0)
                    {
                        cmp = 1;
                        x = x.Right;
                    }
                    else
                    {// Found
                        return (0, x);
                    }
                }
            }
        }

        return (cmp, p);
    }

    /// <summary>
    /// Searches for the first <see cref="OrderedMultiMap{TKey, TValue}.Node"/> with the specified key.
    /// </summary>
    /// <param name="key">The key to search in a collection.</param>
    /// <returns>The first node with the specified key.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Node? FindFirstNode(TKey? key)
    {
        var result = this.SearchFirstNode(this.root, key);
        return result.Cmp == 0 ? result.Leaf : null;
    }

    /// <summary>
    /// Searches for a <see cref="OrderedMultiMap{TKey, TValue}.Node"/> with the specified key/value.
    /// </summary>
    /// <param name="key">The key to search in a collection.</param>
    /// <param name="value">The value to search in a collection.</param>
    /// <returns>The node with the specified key/value.</returns>
    public Node? FindNode(TKey? key, TValue value)
    {
        var result = this.SearchFirstNode(this.root, key);
        if (result.Cmp != 0 || result.Leaf == null)
        {// Not found
            return null;
        }

        var node = result.Leaf;
        if (node.IsSingleNode)
        {// SingleNode
            if (EqualityComparer<TValue>.Default.Equals(node.Value, value))
            {
                return node;
            }
            else
            {
                return null;
            }
        }

        // HeadNode
        while (true)
        {
            if (EqualityComparer<TValue>.Default.Equals(node.Value, value))
            {
                return node;
            }

            node = node.ListNext!;
            if (!node.IsLinkedListNode)
            {// HeadNode
                return null;
            }
        }
    }

    /// <summary>
    /// Searches for the first <see cref="OrderedMap{TKey, TValue}.Node"/> with the key equal to or greater than the specified key (null: all nodes are less than the specified key).
    /// </summary>
    /// <param name="key">The key to search for.</param>
    /// <returns>The first <see cref="OrderedMap{TKey, TValue}.Node"/> with the key equal to or greater than the specified key (null: all nodes are less than the specified key).</returns>
    public Node? GetLowerBound(TKey? key)
    {
        var (cmp, p) = this.SearchFirstNode(this.root, key);

        if (p == null)
        {// Node is null
            return null;
        }
        else if (cmp == 0)
        {// Found
            return p;
        }
        else if (cmp < 0)
        {// Left leaf < key < p
            return p;
        }
        else
        {// p < key < Right leaf
            return p.ListPrevious?.Next ?? p.Next; // NextLeaf
        }
    }

    /// <summary>
    /// Searches for the last <see cref="OrderedMap{TKey, TValue}.Node"/> with the key equal to or lower than the specified key (null: all nodes are greater than the specified key).
    /// </summary>
    /// <param name="key">The key to search for.</param>
    /// <returns>The last <see cref="OrderedMap{TKey, TValue}.Node"/> with the key equal to or lower than the specified key (null: all nodes are greater than the specified key).</returns>
    public Node? GetUpperBound(TKey? key)
    {
        var (cmp, p) = this.SearchFirstNode(this.root, key);

        if (p == null)
        {// Node is null
            return null;
        }
        else if (cmp == 0)
        {// Found
            return p.ListPrevious ?? p;
        }
        else if (cmp < 0)
        {// Left leaf < key < p
            return p.Previous;
        }
        else
        {// p < key < Right leaf
            return p.ListPrevious ?? p;
        }
    }

    /// <summary>
    /// Gets <see cref="Node"/> whose keys are in the range from the lower bound to the upper bound.
    /// </summary>
    /// <param name="lower">Lower bound key.</param>
    /// <param name="upper">Upper bound key.</param>
    /// <returns>The lower and upper <see cref="Node"/>.</returns>
    public (Node? Lower, Node? Upper) GetRange(TKey? lower, TKey? upper)
    {
        var lowerNode = this.GetLowerBound(lower);
        if (lowerNode == null)
        {
            return (null, null);
        }

        var upperNode = this.GetUpperBound(upper);
        if (upperNode == null)
        {
            return (null, null);
        }

        if (this.Comparer.Compare(lowerNode.Key, upperNode.Key) > 0)
        {
            return (null, null);
        }

        return (lowerNode, upperNode);
    }

    /// <summary>
    /// Enumerates <see cref="OrderedMultiMap{TKey, TValue}.Node"/> with the specified key.
    /// </summary>
    /// <param name="key">The key to search in a collection.</param>
    /// <returns>The node with the specified key.</returns>
    public IEnumerable<Node> EnumerateNode(TKey? key)
    {
        var result = this.SearchFirstNode(this.root, key);
        if (result.Cmp != 0 || result.Leaf == null)
        {// Not found
            yield break;
        }

        var node = result.Leaf;
        if (node.IsSingleNode)
        {// SingleNode
            yield return node;
        }
        else
        {// HeadNode
            while (true)
            {
                yield return node;

                node = node.ListNext!;
                if (!node.IsLinkedListNode)
                {// HeadNode
                    yield break;
                }
            }
        }
    }

    /// <summary>
    /// Enumerates <see cref="OrderedMultiMap{TKey, TValue}.Node"/> values with the specified key.
    /// </summary>
    /// <param name="key">The key to search in a collection.</param>
    /// <returns>The node values with the specified key.</returns>
    public IEnumerable<TValue> EnumerateValue(TKey? key)
    {
        var result = this.SearchFirstNode(this.root, key);
        if (result.Cmp != 0 || result.Leaf == null)
        {// Not found
            yield break;
        }

        var node = result.Leaf;
        if (node.IsSingleNode)
        {// SingleNode
            yield return node.Value;
        }
        else
        {// HeadNode
            while (true)
            {
                yield return node.Value;

                node = node.ListNext!;
                if (!node.IsLinkedListNode)
                {// HeadNode
                    yield break;
                }
            }
        }
    }

    /// <summary>
    /// Adds an element to the set.
    /// <br/>O(log n) operation.
    /// </summary>
    /// <param name="key">The element to add to the set.</param>
    /// <returns>node: the added <see cref="OrderedMultiMap{TKey, TValue}.Node"/>.<br/>
    /// NewlyAdded: true if the new key is inserted.</returns>
    private (Node Node, bool NewlyAdded) Probe(TKey key, TValue value, Node? reuse)
    {
        Node? x = this.root; // Traverses tree looking for insertion point.
        Node? p = null; // Parent of x; node at which we are rebalancing.
        int cmp = 0;

        (cmp, p) = this.SearchFirstNode(this.root, key);

        this.version++;
        this.Count++;

        Node n;
        if (reuse != null && reuse.IsUnused)
        {
            reuse.Reset(key, value, NodeColor.Red);
            n = reuse;
        }
        else
        {
            n = new Node(key, value, NodeColor.Red); // Newly inserted node. // this.CreateNode(key, value, NodeColor.Red);
        }

        if (cmp == 0 && p != null)
        {// Found. p is SingleNode or HeadNode.
            n.Color = NodeColor.LinkedList;
            if (p.IsSingleNode)
            {// SingleNode
                p.ListPrevious = n;
                p.ListNext = n;
                n.ListPrevious = p;
                n.ListNext = p;
            }
            else
            {// HeadNode
                n.ListPrevious = p.ListPrevious;
                n.ListNext = p;
                p.ListPrevious!.ListNext = n;
                p.ListPrevious = n;
            }

            return (n, true);
        }

        n.Parent = p;
        if (p != null)
        {
            if (cmp < 0)
            {
                p.Left = n;
            }
            else
            {
                p.Right = n;
            }
        }
        else
        {// Root
            this.root = n;
            n.ColorBlack();
            return (n, true);
        }

        p = n;

#nullable disable
        while (p.Parent != null && p.Parent.IsRed)
        {// p.Parent is not root (root is black), so p.Parent.Parent != null
            if (p.Parent == p.Parent.Parent.Right)
            {
                x = p.Parent.Parent.Left; // uncle
                if (x != null && x.IsRed)
                {
                    x.ColorBlack();
                    p.Parent.ColorBlack();
                    p.Parent.Parent.ColorRed();
                    p = p.Parent.Parent; // loop
                }
                else
                {
                    if (p == p.Parent.Left)
                    {
                        p = p.Parent;
                        this.RotateRight(p);
                    }

                    p.Parent.ColorBlack();
                    p.Parent.Parent.ColorRed();
                    this.RotateLeft(p.Parent.Parent);
                    break;
                }
            }
            else
            {
                x = p.Parent.Parent.Right; // uncle

                if (x != null && x.IsRed)
                {
                    x.ColorBlack();
                    p.Parent.ColorBlack();
                    p.Parent.Parent.ColorRed();
                    p = p.Parent.Parent; // loop
                }
                else
                {
                    if (p == p.Parent.Right)
                    {
                        p = p.Parent;
                        this.RotateLeft(p);
                    }

                    p.Parent.ColorBlack();
                    p.Parent.Parent.ColorRed();
                    this.RotateRight(p.Parent.Parent);
                    break;
                }
            }
        }
#nullable enable

        this.root!.ColorBlack();
        return (n, true);
    }

    #endregion

    #region LowLevel

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void TransplantNode(Node? node, Node destination)
    {// Transplant Node node to Node destination
        if (destination.Parent == null)
        {
            this.root = node;
        }
        else if (destination == destination.Parent.Left)
        {
            destination.Parent.Left = node;
        }
        else
        {
            destination.Parent.Right = node;
        }

        if (node != null)
        {
            node.Parent = destination.Parent;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RotateLeft(Node x)
    {// checked
        var y = x.Right!;
        x.Right = y.Left;
        if (y.Left != null)
        {
            y.Left.Parent = x;
        }

        var p = x.Parent; // Parent of x
        y.Parent = p;
        if (p == null)
        {
            this.root = y;
        }
        else if (x == p.Left)
        {
            p.Left = y;
        }
        else
        {
            p.Right = y;
        }

        y.Left = x;
        x.Parent = y;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RotateRight(Node x)
    {// checked
        var y = x.Left!;
        x.Left = y.Right;
        if (y.Right != null)
        {
            y.Right.Parent = x;
        }

        var p = x.Parent; // Parent of x
        y.Parent = p;
        if (p == null)
        {
            this.root = y;
        }
        else if (x == p.Right)
        {
            p.Right = y;
        }
        else
        {
            p.Left = y;
        }

        y.Right = x;
        x.Parent = y;
    }

    #endregion
}
