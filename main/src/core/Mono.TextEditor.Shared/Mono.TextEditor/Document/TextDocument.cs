﻿// 
// TextDocument.cs
//  
// Author:
//       Mike Krüger <mkrueger@xamarin.com>
// 
// Copyright (c) 2007 Novell, Inc (http://www.novell.com)
// Copyright (c) 2012 Xamarin Inc. (http://xamarin.com)
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Mono.TextEditor.Highlighting;
using Mono.TextEditor.Utils;
using System.Linq;
using System.ComponentModel;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using MonoDevelop.Core.Text;
using MonoDevelop.Ide.Editor;
using MonoDevelop.Core;
using System.IO;
using MonoDevelop.Ide.Editor.Highlighting;
using Microsoft.VisualStudio.Platform;
using Microsoft.VisualStudio.Text.Tagging;

namespace Mono.TextEditor
{
	class TextDocument : ITextDocument
	{
		readonly Microsoft.VisualStudio.Text.ITextBuffer textBuffer;
		bool lineEndingMismatch;
		bool useBOM;
		Encoding encoding;

		//HACK ImmutableText buffer;
		//HACK readonly ILineSplitter splitter;

		ISyntaxHighlighting syntaxMode = null;

		//HACK TextSourceVersionProvider versionProvider = new TextSourceVersionProvider ();

		bool   readOnly;
		ReadOnlyCheckDelegate readOnlyCheckDelegate;

		public string MimeType {
			get {
				return PlatformCatalog.Instance.MimeToContentTypeRegistryService.GetMimeType(this.textBuffer.CurrentSnapshot.ContentType);
			}
			set {
				var newContentType = (value == null) ? null : (PlatformCatalog.Instance.MimeToContentTypeRegistryService.GetContentType(value) ?? PlatformCatalog.Instance.ContentTypeRegistryService.UnknownContentType);
				
				if (this.textBuffer.CurrentSnapshot.ContentType != newContentType) {
					this.textBuffer.ChangeContentType(newContentType, null);
					UpdateSyntaxMode ();
					OnMimeTypeChanged (EventArgs.Empty);
				}
			}
		}

		public event EventHandler MimeTypeChanged;

		protected virtual void OnMimeTypeChanged (EventArgs e)
		{
			EventHandler handler = this.MimeTypeChanged;
			if (handler != null)
				handler (this, e);
		}

		FilePath fileName;
		public FilePath FileName {
			get {
				return fileName;
			}
			set {
				if (fileName != value) {
					fileName = value;
					UpdateSyntaxMode ();
					OnFileNameChanged (EventArgs.Empty);
				}
			}
		}

		public event EventHandler FileNameChanged;

		protected virtual void OnFileNameChanged (EventArgs e)
		{
			EventHandler handler = this.FileNameChanged;
			if (handler != null)
				handler (this, e);
		}

		public bool UseBOM {
			get {
				return this.useBOM;
			}
			set {
				this.useBOM = value;
			}
		}

		public System.Text.Encoding Encoding {
			get {
				return this.encoding;
			}
			set {
				this.encoding = value;
			}
		}

		internal ISyntaxHighlighting SyntaxMode {
			get {
				if (syntaxMode == null) {
					lock (syncObject) {
						InitializeSyntaxMode ();
					}
				}
				return syntaxMode;
			}
			set {
				ISyntaxHighlighting old;
				lock (syncObject) {
					old = syntaxMode;
					if (old != null && old != DefaultSyntaxHighlighting.Instance)
						old.HighlightingStateChanged -= SyntaxMode_HighlightingStateChanged;

					syntaxMode = value;
					if (syntaxMode != null && syntaxMode != DefaultSyntaxHighlighting.Instance)
						syntaxMode.HighlightingStateChanged += SyntaxMode_HighlightingStateChanged;
				}
				OnSyntaxModeChanged (new SyntaxModeChangeEventArgs (old, syntaxMode));
			}
		}

		void SyntaxMode_HighlightingStateChanged (object sender, MonoDevelop.Ide.Editor.LineEventArgs e)
		{
			CommitDocumentUpdate ();
		}

		void OnSyntaxModeChanged (SyntaxModeChangeEventArgs e)
		{
			var handler = SyntaxModeChanged;
			if (handler != null)
				handler (this, e);
		}

		string syntaxModeFileName, syntaxModeMimeType;

		void InitializeSyntaxMode ()
		{
			var def = SyntaxHighlightingService.GetSyntaxHighlightingDefinition (FileName, this.MimeType);
			if (def != null) {
				SyntaxMode = new SyntaxHighlighting (def, this);
			} else {
				SyntaxMode = DefaultSyntaxHighlighting.Instance;
			}
		}

		void UpdateSyntaxMode ()
		{
			//never been initialized, don't need to update
			if (syntaxMode == null) {
				return;
			}

			//already up to date
			if (syntaxModeFileName == fileName && syntaxModeMimeType == this.MimeType) {
				return;
			}
			syntaxModeFileName = fileName;
			syntaxModeMimeType = MimeType;

			InitializeSyntaxMode ();
		}

		internal event EventHandler<SyntaxModeChangeEventArgs> SyntaxModeChanged;

		public object Tag {
			get;
			set;
		}

		public bool HasLineEndingMismatchOnTextSet {
			get {
				return lineEndingMismatch;
			}
			set {
				lineEndingMismatch = value;
			}
		}

		protected TextDocument (bool useBOM, Encoding encoding, string fileName, Microsoft.VisualStudio.Text.ITextBuffer textBuffer)
		{
			this.useBOM = useBOM;
			this.encoding = encoding;
			this.fileName = fileName;

			this.textBuffer = textBuffer;

			this.textBuffer.Properties.AddProperty (typeof (ITextDocument), this);

			this.textBuffer.Changed += this.OnTextBufferChanged;

			TextChanging += HandleSplitterLineSegmentTreeLineRemoved;
			foldSegmentTree.tree.NodeRemoved += HandleFoldSegmentTreetreeNodeRemoved;
			textSegmentMarkerTree.InstallListener (this);
			this.diffTracker.SetTrackDocument (this);
		}

		void OnTextBufferChanged(object sender, Microsoft.VisualStudio.Text.TextContentChangedEventArgs args)
		{
			var textChanged = this.TextChanged;
			if (textChanged != null)
			{
				if (args.Changes != null)
				{
					// Report the changes backwards so that the positions are all accurate
					for (int i = args.Changes.Count - 1; (i >= 0); --i)
					{
						var change = args.Changes[i];

						var textChange = new TextChangeEventArgs(change.OldPosition, change.OldText, change.NewText);
						textChanged(this, textChange);
					}
				}
			}
		}

		void HandleFoldSegmentTreetreeNodeRemoved (object sender, RedBlackTree<FoldSegment>.RedBlackTreeNodeEventArgs e)
		{
			if (e.Node.IsCollapsed)
				foldedSegments.Remove (e.Node);
		}

		public TextDocument () : this (string.Empty)
		{
		}

		public TextDocument (string text) : this(useBOM: false, encoding: Encoding.Default, fileName: null,
												 textBuffer: PlatformCatalog.Instance.TextBufferFactoryService.CreateTextBuffer(text ?? string.Empty,
																																PlatformCatalog.Instance.TextBufferFactoryService.InertContentType))
		{
		}

		public static TextDocument CreateImmutableDocument (string text, bool suppressHighlighting = true)
		{
			return new TextDocument (text) {
				SuppressHighlightUpdate = suppressHighlighting,
				Text = text,
				IsReadOnly = true
			};
		}

		#region Buffer implementation

		public int Length {
			get {
				return this.textBuffer.CurrentSnapshot.Length;
			}
		}

		public bool SuppressHighlightUpdate { get; set; }
		internal DocumentLine longestLineAtTextSet;
		WeakReference cachedText;

		public string Text {
			get {
				string completeText = cachedText != null ? (cachedText.Target as string) : null;
				if (completeText == null) {
					completeText = this.textBuffer.CurrentSnapshot.GetText ();
					cachedText = new WeakReference(completeText);
				}
				return completeText;
			}
			set {
				if (value == null)
					value = "";
				var args = new TextChangeEventArgs(0, Text, value);
				textSegmentMarkerTree.Clear();
				OnTextReplacing(args);
				cachedText = null;
				this.textBuffer.Replace(new Microsoft.VisualStudio.Text.Span(0, this.textBuffer.CurrentSnapshot.Length), value);

				extendingTextMarkers = new List<TextLineMarker>();
				//HACK splitter.Initalize(value, out longestLineAtTextSet);
				ClearFoldSegments();
				//HACK OnTextReplaced(args);
				//HACK versionProvider = new TextSourceVersionProvider();
				//HACK buffer.Version = Version;
				OnTextSet(EventArgs.Empty);
				CommitUpdateAll();
				ClearUndoBuffer();
			}
		}

		public void InsertText (int offset, string text)
		{
			ReplaceText (offset, 0, text);
		}

		public void InsertText (int offset, ITextSource text)
		{
			ReplaceText (offset, 0, text);
		}

		public void RemoveText (int offset, int count)
		{
			ReplaceText (offset, count, (string)null);
		}
		
		public void RemoveText (ISegment segment)
		{
			RemoveText (segment.Offset, segment.Length);
		}

		public void ReplaceText (int offset, int count, ITextSource value)
		{
			ReplaceText (offset, count, value?.Text);
		}

		public void ReplaceText (int offset, int count, string value)
		{
			if (offset < 0)
				throw new ArgumentOutOfRangeException (nameof (offset), "must be > 0, was: " + offset);
			if (offset > Length)
				throw new ArgumentOutOfRangeException (nameof (offset), "must be <= TextLength(" + Length +"), was: " + offset);
			if (count < 0)
				throw new ArgumentOutOfRangeException (nameof (count), "must be > 0, was: " + count);
			if (IsReadOnly)
				return;

			if (value == null)
				value = string.Empty;

			InterruptFoldWorker ();

			var args = new TextChangeEventArgs (offset, count > 0 ? GetTextAt (offset, count) : "", value);

			UndoOperation operation = null;
			bool endUndo = false;
			if (!isInUndo) {
				operation = new UndoOperation (args);
				if (currentAtomicOperation != null) {
					currentAtomicOperation.Add (operation);
				} else {
					OnBeginUndo ();
					undoStack.Push (operation);
					endUndo = true;
				}
				redoStack.Clear ();
			}

			if (value.Length != 0)
				EnsureSegmentIsUnfolded (offset, value.Length);
			
			OnTextReplacing (args);
			//HACK what does this line to? value = args.InsertedText.Text;

			cachedText = null;
			this.textBuffer.Replace(new Microsoft.VisualStudio.Text.Span(offset, count), value);

			//HACK buffer = buffer.RemoveText(offset, count);
			//HACK if (!string.IsNullOrEmpty (value))
			//HACK 	buffer = buffer.InsertText (offset, value);
			foldSegmentTree.UpdateOnTextReplace (this, args);
			//HACK splitter.TextReplaced (this, args);
			//HACK versionProvider.AppendChange (args);
			//HACK buffer.Version = Version;
			//HACK OnTextReplaced(args);
			if (endUndo)
				OnEndUndo (new UndoOperationEventArgs (operation));
		}
		
		public string GetTextBetween (int startOffset, int endOffset)
		{
			if (startOffset < 0)
				throw new ArgumentException ("startOffset < 0");
			if (startOffset > Length)
				throw new ArgumentException ("startOffset > Length");
			if (endOffset < 0)
				throw new ArgumentException ("startOffset < 0");
			if (endOffset > Length)
				throw new ArgumentException ("endOffset > Length");

			return this.textBuffer.CurrentSnapshot.GetText(startOffset, endOffset - startOffset);
		}

		public string GetTextBetween (DocumentLocation start, DocumentLocation end)
		{
			return GetTextBetween (LocationToOffset (start), LocationToOffset (end));
		}
		
		public string GetTextBetween (int startLine, int startColumn, int endLine, int endColumn)
		{
			return GetTextBetween (LocationToOffset (startLine, startColumn), LocationToOffset (endLine, endColumn));
		}
		
		public string GetTextAt (int offset, int count)
		{
			return this.textBuffer.CurrentSnapshot.GetText(offset, count);
		}
		
		public string GetTextAt (DocumentRegion region)
		{
			return GetTextAt (region.GetSegment (this));
		}

		public string GetTextAt (ISegment segment)
		{
			return GetTextAt (segment.Offset, segment.Length);
		}

		/// <summary>
		/// Gets the line text without the delimiter.
		/// </summary>
		/// <returns>
		/// The line text.
		/// </returns>
		/// <param name='line'>
		/// The line number.
		/// </param>
		public string GetLineText (int line)
		{
			var lineSegment = GetLine (line);
			return lineSegment != null ? GetTextAt (lineSegment.Offset, lineSegment.Length) : null;
		}
		
		public string GetLineText (int line, bool includeDelimiter)
		{
			var lineSegment = GetLine (line);
			return GetTextAt (lineSegment.Offset, includeDelimiter ? lineSegment.LengthIncludingDelimiter : lineSegment.Length);
		}
		
		public char GetCharAt (int offset)
		{
			return this.textBuffer.CurrentSnapshot[offset];
		}

		public char GetCharAt (DocumentLocation location)
		{
			return this.textBuffer.CurrentSnapshot[LocationToOffset (location)];
		}

		public char GetCharAt (int line, int column)
		{
			return this.textBuffer.CurrentSnapshot[LocationToOffset (line, column)];
		}

		/// <summary>
		/// Gets the index of the first occurrence of the character in the specified array.
		/// </summary>
		/// <param name="c">Character to search for</param>
		/// <param name="startIndex">Start index of the area to search.</param>
		/// <param name="count">Length of the area to search.</param>
		/// <returns>The first index where the character was found; or -1 if no occurrence was found.</returns>
		public int IndexOf (char c, int startIndex, int count)
		{
			var snapshot = this.textBuffer.CurrentSnapshot;

			for (int i = 0; (i < count); ++i)
			{
				if (snapshot[i + startIndex] == c)
				{
					return i + startIndex;
				}
			}

			return -1;
		}

		/// <summary>
		/// Gets the index of the first occurrence of the specified search text in this text source.
		/// </summary>
		/// <param name="searchText">The search text</param>
		/// <param name="startIndex">Start index of the area to search.</param>
		/// <param name="count">Length of the area to search.</param>
		/// <param name="comparisonType">String comparison to use.</param>
		/// <returns>The first index where the search term was found; or -1 if no occurrence was found.</returns>
		public int IndexOf(string searchText, int startIndex, int count, StringComparison comparisonType)
		{
			//TODO do we really need to handle general StringComparison or should we hard code this only for Ordinal
			// (where we use IndexOf(c, ...) to find possible matches first.
			var snapshot = this.textBuffer.CurrentSnapshot;
			if ((startIndex < 0) || (count < 0) || (startIndex + count > snapshot.Length))
			{
				throw new ArgumentOutOfRangeException(nameof(startIndex));
			}

			if ((count < 0) || (startIndex + count > snapshot.Length) || (startIndex + count < 0))
			{
				throw new ArgumentOutOfRangeException(nameof(count));
			}

			const int bufferSize = 4095;

			int position = startIndex;
			while (position < startIndex + count)
			{
				var end = Math.Min(position + bufferSize + searchText.Length, startIndex + count);
				var text = snapshot.GetText(position, end - position);
				var index = text.IndexOf(searchText, 0, text.Length, comparisonType);
				if (index >= 0)
				{
					return position + index;
				}

				position += (bufferSize + 1);
			}

			return -1;
		}

#if false	//Do we need these?
		/// <summary>
		/// Gets the index of the first occurrence of any character in the specified array.
		/// </summary>
		/// <param name="anyOf">Characters to search for</param>
		/// <param name="startIndex">Start index of the area to search.</param>
		/// <param name="count">Length of the area to search.</param>
		/// <returns>The first index where any character was found; or -1 if no occurrence was found.</returns>
		public int IndexOfAny (char[] anyOf, int startIndex, int count)
		{
			return Text.IndexOfAny (anyOf, startIndex, count);
		}
		
		/// <summary>
		/// Gets the index of the first occurrence of the specified search text in this text source.
		/// </summary>
		/// <param name="searchText">The search text</param>
		/// <param name="startIndex">Start index of the area to search.</param>
		/// <param name="count">Length of the area to search.</param>
		/// <param name="comparisonType">String comparison to use.</param>
		/// <returns>The first index where the search term was found; or -1 if no occurrence was found.</returns>
		public int IndexOf (string searchText, int startIndex, int count, StringComparison comparisonType)
		{
			return Text.IndexOf (searchText, startIndex, count, comparisonType);
		}
		
		/// <summary>
		/// Gets the index of the last occurrence of the specified character in this text source.
		/// </summary>
		/// <param name="c">The search character</param>
		/// <param name="startIndex">Start index of the area to search.</param>
		/// <param name="count">Length of the area to search.</param>
		/// <returns>The last index where the search term was found; or -1 if no occurrence was found.</returns>
		/// <remarks>The search proceeds backwards from (startIndex+count) to startIndex.
		/// This is different than the meaning of the parameters on string.LastIndexOf!</remarks>
		public int LastIndexOf (char c, int startIndex, int count)
		{
			return Text.LastIndexOf (c, startIndex, count);
		}
		
		/// <summary>
		/// Gets the index of the last occurrence of the specified search text in this text source.
		/// </summary>
		/// <param name="searchText">The search text</param>
		/// <param name="startIndex">Start index of the area to search.</param>
		/// <param name="count">Length of the area to search.</param>
		/// <param name="comparisonType">String comparison to use.</param>
		/// <returns>The last index where the search term was found; or -1 if no occurrence was found.</returns>
		/// <remarks>The search proceeds backwards from (startIndex+count) to startIndex.
		/// This is different than the meaning of the parameters on string.LastIndexOf!</remarks>
		public int LastIndexOf (string searchText, int startIndex, int count, StringComparison comparisonType)
		{
			return Text.LastIndexOf (searchText, startIndex, count, comparisonType);
		}
#endif
		//HACk protected virtual void OnTextReplaced (TextChangeEventArgs args)
		//HACk {
		//HACk 	if (TextChanged != null)
		//HACk 		TextChanged (this, args);
		//HACk }
		
		public event EventHandler<TextChangeEventArgs> TextChanged;

		protected virtual void OnTextReplacing (TextChangeEventArgs args)
		{
			if (TextChanging != null)
				TextChanging (this, args);
		}
		public event EventHandler<TextChangeEventArgs> TextChanging;
		
		protected virtual void OnTextSet (EventArgs e)
		{
			EventHandler handler = this.TextSet;
			if (handler != null)
				handler (this, e);
		}
		public event EventHandler TextSet;
		#endregion

		#region Line Splitter operations
		public IEnumerable<DocumentLine> Lines {
			get {
				return this.GetLinesStartingAt(1); }
		}

		public int LineCount {
			get {
				return this.textBuffer.CurrentSnapshot.LineCount;
			}
		}

		public IEnumerable<DocumentLine> GetLinesBetween (int startLine, int endLine)
		{
			var snapshot = this.textBuffer.CurrentSnapshot;

			endLine = Math.Min(endLine, snapshot.LineCount);
			for (int i = startLine; (i <= endLine); ++i)
			{
				yield return this.Get(i);
			}
		}

		public IEnumerable<DocumentLine> GetLinesStartingAt (int startLine)
		{
			return this.GetLinesBetween(startLine, int.MaxValue);
		}

		public IEnumerable<DocumentLine> GetLinesReverseStartingAt (int startLine)
		{
			for (int i = startLine; (i >= 1); --i)
			{
				yield return this.Get(i);
			}
		}

		public int LocationToOffset (int line, int column)
		{
			return LocationToOffset (new DocumentLocation (line, column));
		}
		
		public int LocationToOffset (DocumentLocation location)
		{
//			if (location.Column < DocumentLocation.MinColumn)
//				throw new ArgumentException ("column < MinColumn");
			if (location.Line > this.LineCount || location.Line < DocumentLocation.MinLine)
				return -1;
			DocumentLine line = GetLine (location.Line);
			return System.Math.Min (Length, line.Offset + System.Math.Max (0, System.Math.Min (line.Length, location.Column - 1)));
		}
		
		public DocumentLocation OffsetToLocation (int offset)
		{
			int lineNr = this.OffsetToLineNumber (offset);
			if (lineNr < DocumentLocation.MinLine)
				return DocumentLocation.Empty;
			DocumentLine line = GetLine (lineNr);
			var col = System.Math.Max (1, System.Math.Min (line.LengthIncludingDelimiter, offset - line.Offset) + 1);
			return new DocumentLocation (lineNr, col);
		}

		public string GetLineIndent (int lineNumber)
		{
			return GetLineIndent (GetLine (lineNumber));
		}
		
		public string GetLineIndent (DocumentLine segment)
		{
			if (segment == null)
				return "";
			return segment.GetIndentation (this);
		}
		
		public DocumentLine GetLine (int lineNumber)
		{
			if (lineNumber < DocumentLocation.MinLine)
				return null;
			
			return this.Get (lineNumber);
		}

		IDocumentLine IReadonlyTextDocument.GetLine (int lineNumber)
		{
			return GetLine (lineNumber);
		}

		public DocumentLine GetLineByOffset (int offset)
		{
			return new DocumentLineFromTextSnapshotLine(this.textBuffer.CurrentSnapshot.GetLineFromPosition(offset));
		}

		IDocumentLine IReadonlyTextDocument.GetLineByOffset (int offset)
		{
			return GetLineByOffset (offset);
		}

		public int OffsetToLineNumber (int offset)
		{
			return this.textBuffer.CurrentSnapshot.GetLineFromPosition(offset).LineNumber + 1;
		}
		#endregion

		#region Undo/Redo operations
		public class UndoOperation
		{
			TextChangeEventArgs args;

			public virtual TextChangeEventArgs Args {
				get {
					return args;
				}
			}
			
			public object Tag {
				get;
				set;
			}
			
			protected UndoOperation()
			{
			}

			public UndoOperation (TextChangeEventArgs args)
			{
				this.args = args;
			}

			public virtual void Undo (TextDocument doc, bool fireEvent = true)
			{
				doc.ReplaceText (args.Offset, args.InsertionLength, args.RemovedText.Text);
				if (fireEvent)
					OnUndoDone ();
			}
			
			public virtual void Redo (TextDocument doc, bool fireEvent = true)
			{
				doc.ReplaceText (args.Offset, args.RemovalLength, args.InsertedText.Text);
				if (fireEvent)
					OnRedoDone ();
			}
			
			protected virtual void OnUndoDone ()
			{
				if (UndoDone != null)
					UndoDone (this, EventArgs.Empty);
			}
			public event EventHandler UndoDone;
			
			protected virtual void OnRedoDone ()
			{
				if (RedoDone != null)
					RedoDone (this, EventArgs.Empty);
			}
			public event EventHandler RedoDone;
		}
		
		class AtomicUndoOperation : UndoOperation
		{
			OperationType operationType;
			protected List<UndoOperation> operations = new List<UndoOperation> ();

			public OperationType OperationType {
				get {
					return operationType;
				}
			}
			
			public List<UndoOperation> Operations {
				get {
					return operations;
				}
			}
			
			public override TextChangeEventArgs Args {
				get {
					return null;
				}
			}

			public AtomicUndoOperation (OperationType operationType = OperationType.Undefined)
			{
				this.operationType = operationType;
			}
		

			public void Insert (int index, UndoOperation operation)
			{
				operations.Insert (index, operation);
			}
			
			public void Add (UndoOperation operation)
			{
				operations.Add (operation);
			}
			
			public override void Undo (TextDocument doc, bool fireEvent = true)
			{
				doc.BeginAtomicUndo (operationType);
				try {
					for (int i = operations.Count - 1; i >= 0; i--) {
						operations [i].Undo (doc, false);
						doc.OnUndone (new UndoOperationEventArgs (operations [i]));
					}
				} finally {
					doc.EndAtomicUndo ();
				}
				if (fireEvent)
					OnUndoDone ();
			}
			
			public override void Redo (TextDocument doc, bool fireEvent = true)
			{
				doc.BeginAtomicUndo (operationType);
				try {
					foreach (UndoOperation operation in this.operations) {
						operation.Redo (doc, false);
						doc.OnRedone (new UndoOperationEventArgs (operation));
					}
				} finally {
					doc.EndAtomicUndo ();
				}
				if (fireEvent)
					OnRedoDone ();
			}
		}
		
		class KeyboardStackUndo : AtomicUndoOperation
		{
			bool isClosed = false;
			
			public bool IsClosed {
				get {
					return isClosed;
				}
				set {
					isClosed = value;
				}
			}

			public override TextChangeEventArgs Args {
				get {
					return operations.Count > 0 ? operations [operations.Count - 1].Args : null;
				}
			}
		}
		
		bool isInUndo = false;
		Stack<UndoOperation> undoStack = new Stack<UndoOperation> ();
		Stack<UndoOperation> redoStack = new Stack<UndoOperation> ();
		AtomicUndoOperation currentAtomicOperation = null;

		internal int UndoBeginOffset {
			get {
				if (undoStack.Count == 0)
					return -1;
				var op = undoStack.Peek ();
				while (op is AtomicUndoOperation)
					op = ((AtomicUndoOperation)op).Operations.FirstOrDefault ();
				if (op == null)
					return -1;
				return ((UndoOperation)op).Args.Offset;
			}
		}

		internal int RedoBeginOffset {
			get {
				if (redoStack.Count == 0)
					return -1;
				var op = redoStack.Peek ();
				while (op is AtomicUndoOperation)
					op = ((AtomicUndoOperation)op).Operations.FirstOrDefault ();
				if (op == null)
					return -1;
				return ((UndoOperation)op).Args.Offset;
			}
		}

		public bool CanUndo {
			get {
				return this.undoStack.Count > 0 || currentAtomicOperation != null;
			}
		}
		
		UndoOperation[] savePoint = null;
		public bool IsDirty {
			get {
				if (this.currentAtomicOperation != null)
					return true;
				if (savePoint == null)
					return CanUndo;
				if (undoStack.Count != savePoint.Length) 
					return true;
				UndoOperation[] currentStack = undoStack.ToArray ();
				for (int i = 0; i < currentStack.Length; i++) {
					if (savePoint[i] != currentStack[i])
						return true;
				}
				return false;
			}
		}
		
		public enum LineState {
			Unchanged,
			Dirty,
			Changed
		}

		public DiffTracker diffTracker = new DiffTracker ();

		public DiffTracker DiffTracker {
			get {
				return diffTracker;
			}
			set {
				diffTracker = value;
			}
		}
		
		public LineState GetLineState (DocumentLine line)
		{
			return diffTracker.GetLineState (line);
		}
		
		
		/// <summary>
		/// Marks the document not dirty at this point (should be called after save).
		/// </summary>
		public void SetNotDirtyState ()
		{
			OptimizeTypedUndo ();
			if (undoStack.Count > 0 && undoStack.Peek () is KeyboardStackUndo)
				((KeyboardStackUndo)undoStack.Peek ()).IsClosed = true;
			savePoint = undoStack.ToArray ();
			this.CommitUpdateAll ();
			DiffTracker.SetBaseDocument (CreateDocumentSnapshot ());
		}
		
		public void OptimizeTypedUndo ()
		{
			if (undoStack.Count == 0)
				return;
			UndoOperation top = undoStack.Pop ();
			if (top.Args == null || top.Args.InsertedText == null || top.Args.InsertionLength != 1 || (top is KeyboardStackUndo && ((KeyboardStackUndo)top).IsClosed)) {
				undoStack.Push (top);
				return;
			}
			if (undoStack.Count == 0 || !(undoStack.Peek () is KeyboardStackUndo)) 
				undoStack.Push (new KeyboardStackUndo ());
			var keyUndo = (KeyboardStackUndo)undoStack.Pop ();
			if (keyUndo.IsClosed) {
				undoStack.Push (keyUndo);
				keyUndo = new KeyboardStackUndo ();
			}
			if (keyUndo.Args != null && keyUndo.Args.Offset + 1 != top.Args.Offset || !char.IsLetterOrDigit (top.Args.InsertedText[0])) {
				keyUndo.IsClosed = true;
				undoStack.Push (keyUndo);
				keyUndo = new KeyboardStackUndo ();
			}
			keyUndo.Add (top);
			undoStack.Push (keyUndo);
		}
		
		public int GetCurrentUndoDepth ()
		{
			return undoStack.Count;
		}
		
		public void StackUndoToDepth (int depth)
		{
			if (undoStack.Count == depth)
				return;
			var atomicUndo = new AtomicUndoOperation ();
			while (undoStack.Count > depth) {
				atomicUndo.Operations.Insert (0, undoStack.Pop ());
			}
			undoStack.Push (atomicUndo);
		}
		
		public void MergeUndoOperations (int number)
		{
			number = System.Math.Min (number, undoStack.Count);
			var atomicUndo = new AtomicUndoOperation ();
			while (number-- > 0) {
				atomicUndo.Insert (0, undoStack.Pop ());
			}
			undoStack.Push (atomicUndo);
		}
		
		public void Undo ()
		{
			if (undoStack.Count <= 0)
				return;
			OnBeforeUndoOperation (EventArgs.Empty);
			isInUndo = true;
			var operation = undoStack.Pop ();
			redoStack.Push (operation);
			operation.Undo (this);
			isInUndo = false;
			OnUndone (new UndoOperationEventArgs (operation));
		}

		public void RollbackTo (ITextSourceVersion version)
		{
			var steps = Version.CompareAge (version);
			if (steps < 0)
				throw new InvalidOperationException ("Invalid version");
			while (steps-- > 0) {
				undoStack.Pop ().Undo (this);
			}
		}

		internal protected virtual void OnUndone (UndoOperationEventArgs e)
		{
			EventHandler<UndoOperationEventArgs> handler = this.Undone;
			if (handler != null)
				handler (this, e);
		}

		public event EventHandler<UndoOperationEventArgs> Undone;
		
		internal protected virtual void OnBeforeUndoOperation (EventArgs e)
		{
			var handler = this.BeforeUndoOperation;
			if (handler != null)
				handler (this, e);
		}

		public event EventHandler BeforeUndoOperation;

		public bool CanRedo {
			get {
				return this.redoStack.Count > 0;
			}
		}
		
		public void Redo ()
		{
			if (redoStack.Count <= 0)
				return;
			isInUndo = true;
			UndoOperation operation = redoStack.Pop ();
			undoStack.Push (operation);
			operation.Redo (this);
			isInUndo = false;
			OnRedone (new UndoOperationEventArgs (operation));
		}
		
		internal protected virtual void OnRedone (UndoOperationEventArgs e)
		{
			EventHandler<UndoOperationEventArgs> handler = this.Redone;
			if (handler != null)
				handler (this, e);
		}
		
		public event EventHandler<UndoOperationEventArgs> Redone;
		 
		Stack<OperationType> currentAtomicUndoOperationType =  new Stack<OperationType> ();
		int atomicUndoLevel;

		public bool IsInAtomicUndo {
			get {
				return atomicUndoLevel > 0;
			}
		}

		public OperationType CurrentAtomicUndoOperationType {
			get {
				return currentAtomicUndoOperationType.Count > 0 ?  currentAtomicUndoOperationType.Peek () : OperationType.Undefined;
			}
		}
		
		class UndoGroup : IDisposable
		{
			TextDocument doc;
			
			public UndoGroup (TextDocument doc, OperationType operationType)
			{
				if (doc == null)
					throw new ArgumentNullException ("doc");
				doc.BeginAtomicUndo (operationType);
				this.doc = doc;
			}

			public void Dispose ()
			{
				if (doc != null) {
					doc.EndAtomicUndo ();
					doc = null;
				}
			}
		}
		
		public IDisposable OpenUndoGroup()
		{
			return OpenUndoGroup(OperationType.Undefined);
		}

		public IDisposable OpenUndoGroup(OperationType operationType)
		{
			return new UndoGroup (this, operationType);
		}

		internal void BeginAtomicUndo (OperationType operationType = OperationType.Undefined)
		{
			currentAtomicUndoOperationType.Push (operationType);
			if (currentAtomicOperation == null) {
				Debug.Assert (atomicUndoLevel == 0); 
				currentAtomicOperation = new AtomicUndoOperation (operationType);
				OnBeginUndo ();
			}
			atomicUndoLevel++;
		}

		internal void EndAtomicUndo ()
		{
			if (atomicUndoLevel <= 0)
				throw new InvalidOperationException ("There is no atomic undo operation running.");
			atomicUndoLevel--;
			Debug.Assert (atomicUndoLevel >= 0); 
			
			if (atomicUndoLevel == 0 && currentAtomicOperation != null) {
				if (currentAtomicOperation.Operations.Count > 1) {
					undoStack.Push (currentAtomicOperation);
					OnEndUndo (new UndoOperationEventArgs (currentAtomicOperation));
				} else {
					if (currentAtomicOperation.Operations.Count > 0) {
						undoStack.Push (currentAtomicOperation.Operations [0]);
						OnEndUndo (new UndoOperationEventArgs (currentAtomicOperation.Operations [0]));
					} else {
						OnEndUndo (null);
					}
				}
				currentAtomicOperation = null;
			}
			currentAtomicUndoOperationType.Pop ();
		}
		
		protected virtual void OnBeginUndo ()
		{
			if (BeginUndo != null) 
				BeginUndo (this, EventArgs.Empty);
		}
		
		public void ClearUndoBuffer ()
		{
			undoStack.Clear ();
			redoStack.Clear ();
		}
		
		[Serializable]
		public sealed class UndoOperationEventArgs : EventArgs
		{
			public UndoOperation Operation { get; private set; }

			public UndoOperationEventArgs (UndoOperation operation)
			{
				this.Operation = operation;
			}
			
		}
		
		protected virtual void OnEndUndo (UndoOperationEventArgs e)
		{
			EventHandler<UndoOperationEventArgs> handler = this.EndUndo;
			if (handler != null)
				handler (this, e);
		}
		
		public event EventHandler                         BeginUndo;
		public event EventHandler<UndoOperationEventArgs> EndUndo;
#endregion
		
#region Folding
		
		SegmentTree<FoldSegment> foldSegmentTree = new SegmentTree<FoldSegment> ();
		
		public bool IgnoreFoldings {
			get;
			set;
		}
		
		public bool HasFoldSegments {
			get {
				return FoldSegments.Any ();
			}
		}
		
		public IEnumerable<FoldSegment> FoldSegments {
			get {
				return foldSegmentTree.Segments;
			}
		}
		
		readonly object syncObject = new object();

		CancellationTokenSource foldSegmentSrc;
		object foldSegmentTaskLock = new object ();
		Task foldSegmentTask;

		public void UpdateFoldSegments (List<FoldSegment> newSegments, bool startTask = false, bool useApplicationInvoke = false, CancellationToken masterToken = default(CancellationToken))
		{
			if (newSegments == null) {
				return;
			}
			lock (foldSegmentTaskLock) {
				InterruptFoldWorker ();
				bool update;
				if (!startTask) {
					var newFoldedSegments = UpdateFoldSegmentWorker (newSegments, out update);
					if (useApplicationInvoke) {
						Gtk.Application.Invoke (delegate {
							foldedSegments = newFoldedSegments;
							InformFoldTreeUpdated ();
						});
					} else {
						foldedSegments = newFoldedSegments;
						InformFoldTreeUpdated ();
					}
					return;
				}
				foldSegmentSrc = new CancellationTokenSource ();
				masterToken.Register (InterruptFoldWorker);
				var token = foldSegmentSrc.Token;
				foldSegmentTask = Task.Factory.StartNew (delegate {
					var segments = UpdateFoldSegmentWorker (newSegments, out update, token);
					if (token.IsCancellationRequested)
						return;
					foldedSegments = segments;
					Gtk.Application.Invoke (delegate {
						if (token.IsCancellationRequested)
							return;
						InformFoldTreeUpdated ();
						if (update)
							CommitUpdateAll ();
					});
				}, token);
			}
		}
		
		void RemoveFolding (FoldSegment folding)
		{
			folding.isAttached = false;
			if (folding.isFolded)
				foldedSegments.Remove (folding);
			foldSegmentTree.Remove (folding);
		}
		
		/// <summary>
		/// Updates the fold segments in a background worker thread. Don't call this method outside of a background worker.
		/// Use UpdateFoldSegments instead.
		/// </summary>
		HashSet<FoldSegment> UpdateFoldSegmentWorker (List<FoldSegment> newSegments, out bool update, CancellationToken token = default(CancellationToken))
		{
			var oldSegments = new List<FoldSegment> (FoldSegments);
			int oldIndex = 0;
			bool foldedSegmentAdded = false;
			newSegments.Sort ();
			var newFoldedSegments = new HashSet<FoldSegment> ();
			foreach (FoldSegment newFoldSegment in newSegments) {
				if (token.IsCancellationRequested) {
					update = false;
					return null;
				}
				int offset = newFoldSegment.Offset;
				while (oldIndex < oldSegments.Count && offset > oldSegments [oldIndex].Offset) {
					RemoveFolding (oldSegments [oldIndex]);
					oldIndex++;
				}

				if (oldIndex < oldSegments.Count && offset == oldSegments [oldIndex].Offset) {
					FoldSegment curSegment = oldSegments [oldIndex];
					if (curSegment.IsCollapsed && newFoldSegment.Length != curSegment.Length)
						curSegment.IsCollapsed = newFoldSegment.IsCollapsed = false;
					curSegment.Length = newFoldSegment.Length;
					curSegment.CollapsedText = newFoldSegment.CollapsedText;

					if (newFoldSegment.IsCollapsed) {
						foldedSegmentAdded |= !curSegment.IsCollapsed;
						curSegment.isFolded = true;
					}
					if (curSegment.isFolded)
						newFoldedSegments.Add (curSegment);
					oldIndex++;
				} else {
					newFoldSegment.isAttached = true;
					foldedSegmentAdded |= newFoldSegment.IsCollapsed;
					if (oldIndex < oldSegments.Count && newFoldSegment.Length == oldSegments [oldIndex].Length) {
						newFoldSegment.isFolded = oldSegments [oldIndex].IsCollapsed;
					}
					if (newFoldSegment.IsCollapsed)
						newFoldedSegments.Add (newFoldSegment);
					foldSegmentTree.Add (newFoldSegment);
				}
			}
			while (oldIndex < oldSegments.Count) {
				if (token.IsCancellationRequested) {
					update = false;
					return null;
				}
				RemoveFolding (oldSegments [oldIndex]);
				oldIndex++;
			}
			bool countChanged = foldedSegments.Count != newFoldedSegments.Count;
			update = foldedSegmentAdded || countChanged;
			return newFoldedSegments;
		}
		
		public void WaitForFoldUpdateFinished ()
		{
			if (foldSegmentTask != null) {
				try {
					foldSegmentTask.Wait (5000);
				} catch (AggregateException e) {
					e.Flatten ().Handle (x => x is OperationCanceledException);
				} catch (OperationCanceledException) {
					
				}
				foldSegmentTask = null;
			}
		}
		
		internal void InterruptFoldWorker ()
		{
			if (foldSegmentSrc == null)
				return;
			foldSegmentSrc.Cancel ();
			WaitForFoldUpdateFinished ();
			foldSegmentSrc = null;
		}
		
		public void ClearFoldSegments ()
		{
			InterruptFoldWorker ();
			foldSegmentTree = new SegmentTree<FoldSegment> ();
			foldSegmentTree.tree.NodeRemoved += HandleFoldSegmentTreetreeNodeRemoved; 
			foldedSegments.Clear ();
			InformFoldTreeUpdated ();
		}
		
		public IEnumerable<FoldSegment> GetFoldingsFromOffset (int offset)
		{
			if (offset < 0 || offset >= Length)
				return new FoldSegment[0];
			return foldSegmentTree.GetSegmentsAt (offset);
		}
		
		public IEnumerable<FoldSegment> GetFoldingContaining (int lineNumber)
		{
			return GetFoldingContaining(this.GetLine (lineNumber));
		}
				
		public IEnumerable<FoldSegment> GetFoldingContaining (DocumentLine line)
		{
			if (line == null)
				return new FoldSegment[0];
			return foldSegmentTree.GetSegmentsOverlapping (line.Offset, line.Length);
		}

		public IEnumerable<FoldSegment> GetFoldingContaining (int offset, int length)
		{
			return foldSegmentTree.GetSegmentsOverlapping (offset, length);
		}

		public IEnumerable<FoldSegment> GetStartFoldings (int lineNumber)
		{
			return GetStartFoldings (this.GetLine (lineNumber));
		}
		
		public IEnumerable<FoldSegment> GetStartFoldings (DocumentLine line)
		{
			if (line == null)
				yield break;
			foreach (var fold in GetFoldingContaining (line))
				if (fold.GetStartLine (this) == line)
					yield return fold;
		}

		public IEnumerable<FoldSegment> GetStartFoldings (int offset, int length)
		{
			return GetFoldingContaining (offset, length).Where (fold => offset <= fold.GetStartLine (this).Offset && fold.GetStartLine (this).Offset < offset + length);
		}

		public IEnumerable<FoldSegment> GetEndFoldings (int lineNumber)
		{
			return GetStartFoldings (this.GetLine (lineNumber));
		}
		
		public IEnumerable<FoldSegment> GetEndFoldings (DocumentLine line)
		{
			foreach (FoldSegment segment in GetFoldingContaining (line)) {
				if (segment.GetEndLine (this).Offset == line.Offset)
					yield return segment;
			}
		}

		public IEnumerable<FoldSegment> GetEndFoldings (int offset, int length)
		{
			return GetFoldingContaining (offset, length).Where (fold => offset <= fold.GetEndLine (this).Offset && fold.GetEndLine (this).Offset < offset + length);
		}

		public int GetLineCount (FoldSegment segment)
		{
			return segment.GetEndLine (this).LineNumber - segment.GetStartLine (this).LineNumber;
		}
		
		public void EnsureOffsetIsUnfolded (int offset)
		{
			bool needUpdate = false;
			foreach (FoldSegment fold in GetFoldingsFromOffset (offset).Where (f => f.IsCollapsed && f.Offset < offset && offset < f.EndOffset)) {
				needUpdate = true;
				fold.IsCollapsed = false;
				InformFoldChanged(new FoldSegmentEventArgs(fold));
			}
		}

		public void EnsureSegmentIsUnfolded (int offset, int length)
		{
			bool needUpdate = false;
			foreach (var fold in GetFoldingContaining (offset, length).Where (f => f.IsCollapsed)) {
				needUpdate = true;
				fold.IsCollapsed = false;
				InformFoldChanged(new FoldSegmentEventArgs(fold));
			}
		}

		internal void InformFoldTreeUpdated ()
		{
			var handler = FoldTreeUpdated;
			if (handler != null)
				handler (this, EventArgs.Empty);
		}
		public event EventHandler FoldTreeUpdated;
		
		HashSet<FoldSegment> foldedSegments = new HashSet<FoldSegment> ();

		public IEnumerable<FoldSegment> FoldedSegments {
			get {
				return foldedSegments;
			}
		}

		internal void InformFoldChanged (FoldSegmentEventArgs args)
		{
			if (args.FoldSegment.IsCollapsed) {
				foldedSegments.Add (args.FoldSegment);
			} else {
				foldedSegments.Remove (args.FoldSegment);
			}
			var handler = Folded;
			if (handler != null)
				handler (this, args);
		}

		public event EventHandler<FoldSegmentEventArgs> Folded;
#endregion

#region Text line markers

		public event EventHandler<TextMarkerEvent> MarkerAdded;
		protected virtual void OnMarkerAdded (TextMarkerEvent e)
		{
			EventHandler<TextMarkerEvent> handler = this.MarkerAdded;
			if (handler != null)
				handler (this, e);
		}

		public event EventHandler<TextMarkerEvent> MarkerRemoved;
		protected virtual void OnMarkerRemoved (TextMarkerEvent e)
		{
			EventHandler<TextMarkerEvent> handler = this.MarkerRemoved;
			if (handler != null)
				handler (this, e);
		}

		
		List<TextLineMarker> extendingTextMarkers = new List<TextLineMarker> ();
		public IEnumerable<DocumentLine> LinesWithExtendingTextMarkers {
			get {
				return from marker in extendingTextMarkers where marker.LineSegment != null select marker.LineSegment;
			}
		}
		
		public void AddMarker (int lineNumber, TextLineMarker marker)
		{
			AddMarker (this.GetLine (lineNumber), marker);
		}
		
		public void AddMarker (DocumentLine line, TextLineMarker marker)
		{
			AddMarker (line, marker, true);
		}

		public void AddMarker (DocumentLine line, TextLineMarker marker, bool commitUpdate, int idx = -1)
		{
			if (line == null || marker == null)
				return;
			line.AddMarker (marker, idx);
			OnMarkerAdded (new TextMarkerEvent (line, marker));
			if (marker is IExtendingTextLineMarker) {
				lock (extendingTextMarkers) {
					extendingTextMarkers.Add (marker);
					extendingTextMarkers.Sort (CompareMarkers);
					OnHeightChanged (EventArgs.Empty);
				}
			}
			if (commitUpdate)
				this.CommitLineUpdate (line);
		}
		
		static int CompareMarkers (TextLineMarker left, TextLineMarker right)
		{
			if (left.LineSegment == null || right.LineSegment == null)
				return 0;
			return left.LineSegment.Offset.CompareTo (right.LineSegment.Offset);
		}
		
		public void RemoveMarker (TextLineMarker marker)
		{
			RemoveMarker (marker, true);
		}
		
		public void RemoveMarker (TextLineMarker marker, bool updateLine)
		{
			if (marker == null)
				return;
			var line = marker.LineSegment;
			if (line == null)
				return;
			if (marker is IDisposable)
				((IDisposable)marker).Dispose ();
			
			line.RemoveMarker (marker);
			OnMarkerRemoved (new TextMarkerEvent (line, marker));
			if (marker is IExtendingTextLineMarker) {
				lock (extendingTextMarkers) {
					extendingTextMarkers.Remove (marker);
					OnHeightChanged (EventArgs.Empty);
				}
			}
			if (updateLine)
				this.CommitLineUpdate (line);
		}
		
		public void RemoveMarker (int lineNumber, Type type)
		{
			RemoveMarker (this.GetLine (lineNumber), type);
		}
		
		public void RemoveMarker (DocumentLine line, Type type)
		{
			RemoveMarker (line, type, true);
		}
		
		public void RemoveMarker (DocumentLine line, Type type, bool updateLine)
		{
			if (line == null || type == null)
				return;
			line.RemoveMarker (type);
			if (typeof (IExtendingTextLineMarker).IsAssignableFrom (type)) {
				lock (extendingTextMarkers) {
					foreach (TextLineMarker marker in line.Markers.Where (marker => marker is IExtendingTextLineMarker)) {
						extendingTextMarkers.Remove (marker);
					}
					OnHeightChanged (EventArgs.Empty);
				}
			}
			if (updateLine)
				this.CommitLineUpdate (line);
		}

#endregion

#region Text segment markers

		int textSegmentInsertId = 0;
		SegmentTree<TextSegmentMarker> textSegmentMarkerTree = new SegmentTree<TextSegmentMarker> ();

		public static IEnumerable<TextSegmentMarker> OrderTextSegmentMarkersByInsertion (IEnumerable<TextSegmentMarker> enumerable)
		{
			return enumerable.OrderBy (m => m.insertId);
		}

		public IEnumerable<TextSegmentMarker> GetTextSegmentMarkersAt (DocumentLine line)
		{
			return textSegmentMarkerTree.GetSegmentsOverlapping (line.Segment);
		}

		internal IEnumerable<TextSegmentMarker> GetVisibleTextSegmentMarkersAt (DocumentLine line)
		{
			foreach (var marker in textSegmentMarkerTree.GetSegmentsOverlapping (line.Segment))
				if (marker.IsVisible)
					yield return marker;
		}

		public IEnumerable<TextSegmentMarker> GetTextSegmentMarkersAt (ISegment segment)
		{
			return textSegmentMarkerTree.GetSegmentsOverlapping (segment);
		}

		public IEnumerable<TextSegmentMarker> GetTextSegmentMarkersAt (int offset)
		{
			return textSegmentMarkerTree.GetSegmentsAt (offset);
		}
		

		public void AddMarker (TextSegmentMarker marker)
		{
			marker.insertId = textSegmentInsertId++;
			textSegmentMarkerTree.Add (marker);
			var startLine = OffsetToLineNumber (marker.Offset);
			var endLine = OffsetToLineNumber (marker.EndOffset);
			CommitMultipleLineUpdate (startLine, endLine);
		}

		/// <summary>
		/// Removes a marker from the document.
		/// </summary>
		/// <returns><c>true</c>, if marker was removed, <c>false</c> otherwise.</returns>
		/// <param name="marker">Marker.</param>
		public bool RemoveMarker (TextSegmentMarker marker)
		{
			bool wasRemoved = textSegmentMarkerTree.Remove (marker);
			if (wasRemoved) {
				var startLine = OffsetToLineNumber (marker.Offset);
				var endLine = OffsetToLineNumber (marker.EndOffset);
				CommitMultipleLineUpdate (startLine, endLine);
			}
			return wasRemoved;
		}

		#endregion

		void HandleSplitterLineSegmentTreeLineRemoved (object sender, TextChangeEventArgs e)
		{
			var line = GetLineByOffset (e.Offset);
			var endOffset = e.Offset + e.RemovalLength;
			var offset = line.Offset;
			do {
				foreach (TextLineMarker marker in line.Markers) {
					if (marker is IExtendingTextLineMarker) {
						UnRegisterVirtualTextMarker ((IExtendingTextLineMarker)marker);
						lock (extendingTextMarkers) {
							extendingTextMarkers.Remove (marker);
							OnHeightChanged (EventArgs.Empty);
						}
					}
				}
				offset += line.LengthIncludingDelimiter;
				line = line.NextLine;
			} while (line != null && offset < endOffset);
		}

		public bool Contains (int offset)
		{
			return new TextSegment (0, Length).Contains (offset);
		}
		
		public bool Contains (ISegment segment)
		{
			return new TextSegment (0, Length).Contains (segment);
		}
		
		
#region Update logic
		List<DocumentUpdateRequest> updateRequests = new List<DocumentUpdateRequest> ();
		
		public IEnumerable<DocumentUpdateRequest> UpdateRequests {
			get {
				return updateRequests;
			}
		}
		// Use CanEdit (int lineNumber) instead for getting a request
		// if a part of a document can be read. ReadOnly should generally not be used
		// for deciding, if a document is readonly or not.
		public bool IsReadOnly {
			get {
				return readOnly;
			}
			set {
				readOnly = value;
			}
		}
		
		public ReadOnlyCheckDelegate ReadOnlyCheckDelegate {
			get { return readOnlyCheckDelegate; }
			set { readOnlyCheckDelegate = value; }
		}


		public void RequestUpdate (DocumentUpdateRequest request)
		{
			lock (syncObject) {
				updateRequests.Add (request);
			}
		}
		
		public void CommitDocumentUpdate ()
		{
			lock (syncObject) {
				if (DocumentUpdated != null)
					DocumentUpdated (this, EventArgs.Empty);
				updateRequests.Clear ();
			}
		}
		
		public void CommitLineUpdate (int line)
		{
			RequestUpdate (new LineUpdate (line));
			CommitDocumentUpdate ();
		}
		
		public void CommitLineUpdate (DocumentLine line)
		{
			CommitLineUpdate (line.LineNumber);
		}

		public void CommitUpdateAll ()
		{
			RequestUpdate (new UpdateAll ());
			CommitDocumentUpdate ();
		}

		public void CommitMultipleLineUpdate (int start, int end)
		{
			RequestUpdate (new MultipleLineUpdate (start, end));
			CommitDocumentUpdate ();
		}
		
		public event EventHandler DocumentUpdated;
#endregion

#region Helper functions
		public const string openBrackets    = "([{<";
		public const string closingBrackets = ")]}>";
		
		public static bool IsBracket (char ch)
		{
			return (openBrackets + closingBrackets).IndexOf (ch) >= 0;
		}
		
		public static bool IsWordSeparator (char ch)
		{
			return !(char.IsLetterOrDigit (ch) || ch == '_');
		}

		public bool IsWholeWordAt (int offset, int length)
		{
			return (offset == 0 || IsWordSeparator (GetCharAt (offset - 1))) &&
				   (offset + length == Length || IsWordSeparator (GetCharAt (offset + length)));
		}
		
		public bool IsEmptyLine (DocumentLine line)
		{
			for (int i = 0; i < line.Length; i++) {
				char ch = GetCharAt (line.Offset + i);
				if (!Char.IsWhiteSpace (ch)) 
					return false;
			}
			return true;
		}

		public enum CharacterClass {
			Unknown,

			Whitespace,

			IdentifierPart

		}
		

		public static CharacterClass GetCharacterClass (char ch)

		{
			if (Char.IsWhiteSpace (ch))
				return CharacterClass.Whitespace;
			if (Char.IsLetterOrDigit (ch) || ch == '_')
				return CharacterClass.IdentifierPart;

			return CharacterClass.Unknown;

		}
		
		public static void RemoveTrailingWhitespaces (TextEditorData data, DocumentLine line)
		{
			if (line == null)
				return;
			int whitespaces = 0;
			for (int i = line.Length - 1; i >= 0; i--) {
				if (Char.IsWhiteSpace (data.Document.GetCharAt (line.Offset + i))) {
					whitespaces++;
				} else {
					break;
				}
			}
			
			if (whitespaces > 0) {
				var removeOffset = line.Offset + line.Length - whitespaces;
				data.Remove (removeOffset, whitespaces);
			}
		}
#endregion

		public bool IsInUndo {
			get {
				return isInUndo;
			}
		}
		
		Dictionary<int, IExtendingTextLineMarker> virtualTextMarkers = new Dictionary<int, IExtendingTextLineMarker> ();
		public void RegisterVirtualTextMarker (int lineNumber, IExtendingTextLineMarker marker)
		{
			virtualTextMarkers[lineNumber] = marker;
		}
		
		public IExtendingTextLineMarker GetExtendingTextMarker (int lineNumber)
		{
			IExtendingTextLineMarker result;
			if (virtualTextMarkers.TryGetValue (lineNumber, out result))
				return result;
			return null;
		}
		
		/// <summary>
		/// un register virtual text marker.
		/// </summary>
		/// <param name='marker'>
		/// marker.
		/// </param>
		public void UnRegisterVirtualTextMarker (IExtendingTextLineMarker marker)
		{
			var keys = new List<int> (from pair in virtualTextMarkers where pair.Value == marker select pair.Key);
			keys.ForEach (key => { virtualTextMarkers.Remove (key); CommitLineUpdate (key); });
		}
		
		
#region Diff


		int[] GetDiffCodes (ref int codeCounter, Dictionary<string, int> codeDictionary, bool includeEol)
		{
			int i = 0;
			var result = new int[LineCount];
			foreach (DocumentLine line in Lines) {
				string lineText = this.GetTextAt (line.Offset, includeEol ? line.LengthIncludingDelimiter : line.Length);
				int curCode;
				if (!codeDictionary.TryGetValue (lineText, out curCode)) {
					codeDictionary[lineText] = curCode = ++codeCounter;
				}
				result[i] = curCode;
				i++;
			}
			return result;
		}
		
		public IEnumerable<Hunk> Diff (TextDocument changedDocument, bool includeEol = true)
		{
			var codeDictionary = new Dictionary<string, int> ();
			int codeCounter = 0;
			return Mono.TextEditor.Utils.Diff.GetDiff<int> (this.GetDiffCodes (ref codeCounter, codeDictionary, includeEol),
				changedDocument.GetDiffCodes (ref codeCounter, codeDictionary, includeEol));
		}
#endregion

		

#region ContentLoaded 
		// The problem: Action to perform on a newly opened text editor, but content didn't get loaded because autosave file exist.
		//              At this point the document is open, but the content didn't yet have loaded - therefore the action on the conent can't be perfomed.
		// Solution: Perform the action after the user did choose load autosave or not. 
		//           This is done by the RunWhenLoaded method. Text editors should call the InformLoadComplete () when the content has successfully been loaded
		//           at that point the outstanding actions are run.
		bool isLoaded;
		List<Action> loadedActions = new List<Action> ();
		List<Action> realizedActions = new List<Action> ();
		
		/// <summary>
		/// Gets a value indicating whether this instance is loaded.
		/// </summary>
		/// <value>
		/// <c>true</c> if this instance is loaded; otherwise, <c>false</c>.
		/// </value>
		public bool IsLoaded {
			get { return isLoaded; }
		}

		public bool IsRealized {
			get;
			private set;
		}
		
		/// <summary>
		/// Informs the document when the content is loaded. All outstanding actions are executed.
		/// </summary>
		public void InformLoadComplete ()
		{
			if (isLoaded)
				return;
			isLoaded = true;
			loadedActions.ForEach (act => act ());
			loadedActions = null;
		}

		public void InformRealizedComplete ()
		{
			if (IsRealized)
				return;

			IsRealized = true;
			realizedActions.ForEach (act => act ());
			realizedActions = null;
		}
		
		/// <summary>
		/// Performs an action when the content is loaded.
		/// </summary>
		/// <param name='action'>
		/// The action to run.
		/// </param>
		public void RunWhenLoaded (Action action)
		{
			if (IsLoaded) {
				action ();
				return;
			}
			loadedActions.Add (action);
		}

		public void RunWhenRealized (Action action)
		{
			if (IsRealized) {
				action ();
				return;
			}
			realizedActions.Add (action);
		}
#endregion

#region ITextSource implementation

		public System.IO.TextReader CreateReader ()
		{
			var snapshot = this.textBuffer.CurrentSnapshot;
			return new SnapshotSpanToTextReader(new Microsoft.VisualStudio.Text.SnapshotSpan(snapshot, 0, snapshot.Length));
		}

		public System.IO.TextReader CreateReader (int offset, int length)
		{
			var snapshot = this.textBuffer.CurrentSnapshot;
			return new SnapshotSpanToTextReader(new Microsoft.VisualStudio.Text.SnapshotSpan(snapshot, offset, length));
		}

		public virtual ITextSourceVersion Version {
			get {
				return new TextVersionToTextSourceVersion(this.textBuffer.CurrentSnapshot.Version);
			}
		}

		public char this [int offset] {
			get {
				return GetCharAt (offset);
			}
			set {
				ReplaceText (offset, 1, value.ToString ());
			}
		}


		public class SnapshotDocument : TextDocument
		{
			readonly ITextSourceVersion version;
			public override ITextSourceVersion Version  {
				get {
					return version;
				}
			}


			public SnapshotDocument (TextDocument doc) : base (doc.useBOM, doc.encoding, doc.fileName,
															   CreateBufferFromTextDocument(doc))
			{
				this.version = doc.Version;
				//HACK ((LazyLineSplitter)splitter).src = this;

				IsReadOnly = true;
			}

			private static Microsoft.VisualStudio.Text.ITextBuffer CreateBufferFromTextDocument(TextDocument doc)
			{
				var snapshot = doc.textBuffer.CurrentSnapshot;
				return PlatformCatalog.Instance.TextBufferFactoryService.CreateTextBuffer(new Microsoft.VisualStudio.Text.SnapshotSpan(snapshot, 0, snapshot.Length),
																						  snapshot.ContentType);
			}
		}

		public TextDocument CreateDocumentSnapshot ()
		{
			return new SnapshotDocument (this);
		}

		public void CopyTo (int sourceIndex, char [] destination, int destinationIndex, int count)
		{
			var snapshot = this.textBuffer.CurrentSnapshot;
			for (int i = 0; (i < count); ++i)
			{
				destination[destinationIndex + i] = snapshot[sourceIndex + i];
			}
		}


		ITextSource ITextSource.CreateSnapshot ()
		{
			var snapshot = this.textBuffer.CurrentSnapshot;
			return new SnapshotSpanToTextSource(this.UseBOM, this.Encoding, new Microsoft.VisualStudio.Text.SnapshotSpan(snapshot, 0, snapshot.Length));
		}

		ITextSource ITextSource.CreateSnapshot (int offset, int length)
		{
			var snapshot = this.textBuffer.CurrentSnapshot;
			return new SnapshotSpanToTextSource(this.UseBOM, this.Encoding, new Microsoft.VisualStudio.Text.SnapshotSpan(snapshot, offset, length));
		}

		IReadonlyTextDocument ITextDocument.CreateDocumentSnapshot ()
		{
			return CreateDocumentSnapshot ();
		}

		public void WriteTextTo (TextWriter writer)
		{
			if (writer == null)
				throw new ArgumentNullException ("writer");
			writer.Write (Text);
		}

		public void WriteTextTo (TextWriter writer, int offset, int length)
		{
			if (writer == null)
				throw new ArgumentNullException ("writer");
			writer.Write (GetTextAt (offset, length));
		}

#endregion

		void OnHeightChanged (EventArgs e)
		{
			HeightChanged?.Invoke (this, e);
		}

		internal event EventHandler HeightChanged;

		private DocumentLine Get(int number)
		{
			return new DocumentLineFromTextSnapshotLine(this.textBuffer.CurrentSnapshot.GetLineFromLineNumber(number - 1));
		}

		sealed class DocumentLineFromTextSnapshotLine : DocumentLine
		{
			readonly Microsoft.VisualStudio.Text.ITextSnapshotLine line;

			public override int Offset
			{
				get { return this.line.Start; }
				set
				{

				}
			}

			public override int LineNumber
			{
				get
				{
					return this.line.LineNumber + 1;
				}
			}

			public override DocumentLine NextLine
			{
				get
				{
					int newLineNumber = this.line.LineNumber + 1;
					return (newLineNumber < this.line.Snapshot.LineCount) ? new DocumentLineFromTextSnapshotLine(this.line.Snapshot.GetLineFromLineNumber(newLineNumber)) : null;
				}
			}

			public override DocumentLine PreviousLine
			{
				get
				{
					int newLineNumber = this.line.LineNumber - 1;
					return (newLineNumber >= 0) ? new DocumentLineFromTextSnapshotLine(this.line.Snapshot.GetLineFromLineNumber(newLineNumber)) : null;
				}
			}

			public DocumentLineFromTextSnapshotLine(Microsoft.VisualStudio.Text.ITextSnapshotLine line) : base(line.LengthIncludingLineBreak, DocumentLineFromTextSnapshotLine.LineCode(line))
			{
				this.line = line;
			}

			public override string ToString()
			{
				return string.Format("[LineSegment: lineNumber={0}, Offset={1}]", this.line.LineNumber, this.line.Start.Position);
			}

			private static UnicodeNewline LineCode(Microsoft.VisualStudio.Text.ITextSnapshotLine line)
			{
				if (line.LineBreakLength == 2)
				{
					return UnicodeNewline.CRLF;
				}
				else if (line.LineBreakLength == 0)
				{
					return UnicodeNewline.Unknown;
				}
				else
				{
					switch(line.Snapshot[line.End])
					{
						case '\u000A': return UnicodeNewline.LF;
						case '\u000B': return UnicodeNewline.VT; // Not recognized by VS
						case '\u000C': return UnicodeNewline.FF; // Not recognized by VS

						case '\u000D': return UnicodeNewline.CR;
						case '\u0085': return UnicodeNewline.NEL;
						case '\u2028': return UnicodeNewline.LS;
						case '\u2029': return UnicodeNewline.PS;
						default: return UnicodeNewline.Unknown;
					}
				}
			}

			public override int GetHashCode()
			{
				return this.line.Snapshot.GetHashCode() ^ this.line.LineNumber;
			}

			public override bool Equals(object other)
			{
				var otherLine = other as DocumentLineFromTextSnapshotLine;
				return (otherLine != null) && (otherLine.line.Snapshot == this.line.Snapshot) && (otherLine.line.LineNumber == this.line.LineNumber);
			}
		}

		class SnapshotSpanToTextSource : ITextSource
		{
			private readonly Microsoft.VisualStudio.Text.SnapshotSpan span;

			public SnapshotSpanToTextSource(bool useBOM, Encoding encoding, Microsoft.VisualStudio.Text.SnapshotSpan span)
			{
				this.UseBOM = useBOM;
				this.Encoding = encoding;
				this.span = span;
			}

			public ITextSourceVersion Version { get { return null; } }

			/// <summary>
			/// Determines if a byte order mark was read or is going to be written.
			/// </summary>
			public bool UseBOM { get; }

			/// <summary>
			/// Encoding of the text that was read from or is going to be saved to.
			/// </summary>
			public Encoding Encoding { get; }

			/// <summary>
			/// Gets the total text length.
			/// </summary>
			/// <returns>The length of the text, in characters.</returns>
			/// <remarks>This is the same as Text.Length, but is more efficient because
			///  it doesn't require creating a String object.</remarks>
			public int Length { get { return this.span.Length; } }

			/// <summary>
			/// Gets the whole text as string.
			/// </summary>
			[System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Naming", "CA1721:PropertyNamesShouldNotMatchGetMethods")]
			public string Text { get { return this.span.GetText(); } }

			/// <summary>
			/// Gets a character at the specified position in the document.
			/// </summary>
			/// <paramref name="offset">The index of the character to get.</paramref>
			/// <exception cref="ArgumentOutOfRangeException">Offset is outside the valid range (0 to TextLength-1).</exception>
			/// <returns>The character at the specified position.</returns>
			/// <remarks>This is the same as Text[offset], but is more efficient because
			///  it doesn't require creating a String object.</remarks>
			public char this[int offset] { get { return this.span.Snapshot[offset + this.span.Start.Position]; } }

			/// <summary>
			/// Gets a character at the specified position in the document.
			/// </summary>
			/// <paramref name="offset">The index of the character to get.</paramref>
			/// <exception cref="ArgumentOutOfRangeException">Offset is outside the valid range (0 to TextLength-1).</exception>
			/// <returns>The character at the specified position.</returns>
			/// <remarks>This is the same as Text[offset], but is more efficient because
			///  it doesn't require creating a String object.</remarks>
			public char GetCharAt(int offset) { return this.span.Snapshot[offset + this.span.Start.Position]; }

			/// <summary>
			/// Retrieves the text for a portion of the document.
			/// </summary>
			/// <exception cref="ArgumentOutOfRangeException">offset or length is outside the valid range.</exception>
			/// <remarks>This is the same as Text.Substring, but is more efficient because
			///  it doesn't require creating a String object for the whole document.</remarks>
			public string GetTextAt(int offset, int length) { return this.span.Snapshot.GetText(offset + this.span.Start.Position, length); }

			/// <summary>
			/// Creates a new TextReader to read from this text source.
			/// </summary>
			public TextReader CreateReader() { return null; }

			/// <summary>
			/// Creates a new TextReader to read from this text source.
			/// </summary>
			public TextReader CreateReader(int offset, int length) { return null; }

			/// <summary>
			/// Writes the text from this document into the TextWriter.
			/// </summary>
			public void WriteTextTo(TextWriter writer)
			{
				this.WriteTextTo(writer, 0, this.span.Length);
			}

			/// <summary>
			/// Writes the text from this document into the TextWriter.
			/// </summary>
			public void WriteTextTo(TextWriter writer, int offset, int length)
			{
				for (int i = 0; (i < length); ++i)
				{
					writer.Write(this.span.Snapshot[this.span.Start.Position + +offset + i]);
				}
			}

			/// <summary>
			/// Copies text from the source index to a destination array at destinationIndex.
			/// </summary>
			/// <param name="sourceIndex">The start offset copied from.</param>
			/// <param name="destination">The destination array copied to.</param>
			/// <param name="destinationIndex">The destination index copied to.</param>
			/// <param name="count">The number of characters to be copied.</param>
			public void CopyTo(int sourceIndex, char[] destination, int destinationIndex, int count)
			{
				for (int i = 0; (i < count); ++i)
				{
					destination[destinationIndex + i] = this.span.Snapshot[this.span.Start.Position + i];
				}
			}

			/// <summary>
			/// Creates an immutable snapshot of this text source.
			/// Unlike all other methods in this interface, this method is thread-safe.
			/// </summary>
			public ITextSource CreateSnapshot() { return this; }

			/// <summary>
			/// Creates an immutable snapshot of a part of this text source.
			/// Unlike all other methods in this interface, this method is thread-safe.
			/// </summary>
			public ITextSource CreateSnapshot(int offset, int length)
			{
				return new SnapshotSpanToTextSource(this.UseBOM, this.Encoding, new Microsoft.VisualStudio.Text.SnapshotSpan(this.span.Snapshot, this.span.Start.Position + offset, length));
			}
		}

		sealed class SnapshotSpanToTextReader : TextReader
		{
			private readonly Microsoft.VisualStudio.Text.SnapshotSpan span;
			private int index;
			public SnapshotSpanToTextReader(Microsoft.VisualStudio.Text.SnapshotSpan span)
			{
				this.span = span;
			}

			public override int Peek()
			{
				if (index >= this.span.Length)
					return -1;
				return this.span.Snapshot[this.span.Start.Position + index];
			}

			public override int Read()
			{
				if (index >= this.span.Length)
					return -1;
				return this.span.Snapshot[this.span.Start.Position + index++];
			}

			public override int Read(char[] buffer, int index, int count)
			{
				count = System.Math.Min(this.index + count, this.span.Length) - this.index;
				if (count <= 0)
					return 0;

				for (int i = 0; (i < count); ++i)
				{
					buffer[i] = this.span.Snapshot[this.span.Start.Position + i];
				}

				this.index += count;
				return count;
			}
		}

		public class TextVersionToTextSourceVersion : ITextSourceVersion
		{
			private readonly Microsoft.VisualStudio.Text.ITextVersion version;

			public TextVersionToTextSourceVersion(Microsoft.VisualStudio.Text.ITextVersion version)
			{
				this.version = version;
			}

			public bool BelongsToSameDocumentAs(ITextSourceVersion other)
			{
				return (other as TextVersionToTextSourceVersion)?.version.TextBuffer == this.version.TextBuffer;
			}

			public int CompareAge(ITextSourceVersion other)
			{
				var otherVersion = other as TextVersionToTextSourceVersion;
				if (otherVersion?.version.TextBuffer != this.version.TextBuffer)
				{
					throw new ArgumentException(nameof(other) + " is from a different document");
				}

				int cmp = this.version.VersionNumber - otherVersion.version.VersionNumber;
				return (cmp > 0) ? 1 : ((cmp == 0) ? 0 : -1);
			}

			/// <summary>
			/// Gets the changes from this checkpoint to the other checkpoint.
			/// If 'other' is older than this checkpoint, reverse changes are calculated.
			/// </summary>
			/// <remarks>This method is thread-safe.</remarks>
			/// <exception cref="System.ArgumentException">Raised if 'other' belongs to a different document than this checkpoint.</exception>
			public IEnumerable<TextChangeEventArgs> GetChangesTo(ITextSourceVersion other)
			{
				var otherVersion = other as TextVersionToTextSourceVersion;
				if (otherVersion?.version.TextBuffer != this.version.TextBuffer)
				{
					throw new ArgumentException(nameof(other) + " is from a different document");
				}

				int cmp = this.version.VersionNumber - otherVersion.version.VersionNumber;
				if (cmp > 0)
				{
					var v = otherVersion.version;
					while (v != this.version)
					{
						if (v.Changes != null)
						{
							for (int i = v.Changes.Count - 1; (i >= 0); --i)
							{
								var change = v.Changes[i];
								yield return new TextChangeEventArgs(change.OldPosition, change.OldText, change.NewText);
							}
						}

						v = v.Next;
					}
				}
				else
				{
					// Calculate the changes from the (older) this to the (newer) other & return in reverse order.
					var changes = new List<TextChangeEventArgs>(other.GetChangesTo(this));
					for (int i = changes.Count - 1; (i >= 0); --i)
					{
						yield return changes[i];
					}
				}
			}

			/// <summary>
			/// Calculates where the offset has moved in the other buffer version.
			/// </summary>
			/// <exception cref="System.ArgumentException">Raised if 'other' belongs to a different document than this checkpoint.</exception>
			public int MoveOffsetTo(ITextSourceVersion other, int oldOffset)
			{
				var otherVersion = other as TextVersionToTextSourceVersion;
				if (otherVersion?.version.TextBuffer != this.version.TextBuffer)
				{
					throw new ArgumentException(nameof(other) + " is from a different document");
				}

				int cmp = this.version.VersionNumber - otherVersion.version.VersionNumber;
				if (cmp == 0)
				{
					return oldOffset;
				}

				if (cmp > 0)
				{
					return Microsoft.VisualStudio.Text.Tracking.TrackPositionBackwardInTime(Microsoft.VisualStudio.Text.PointTrackingMode.Positive,
																		oldOffset,
																		this.version, otherVersion.version);
				}
				else
				{
					return Microsoft.VisualStudio.Text.Tracking.TrackPositionForwardInTime(Microsoft.VisualStudio.Text.PointTrackingMode.Positive,
																	   oldOffset,
																	   this.version, otherVersion.version);
				}
			}
		}
	}

	delegate bool ReadOnlyCheckDelegate (int line);
}
