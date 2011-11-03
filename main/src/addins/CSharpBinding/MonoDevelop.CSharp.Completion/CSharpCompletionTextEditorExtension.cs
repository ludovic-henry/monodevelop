// 
// CSharpCompletionTextEditorExtension.cs
//  
// Author:
//       Mike Krüger <mkrueger@xamarin.com>
// 
// Copyright (c) 2011 Xamarin <http://xamarin.com>
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
using MonoDevelop.Ide.Gui.Content;
using ICSharpCode.NRefactory.CSharp;
using MonoDevelop.Core;
using MonoDevelop.Ide.CodeCompletion;
using ICSharpCode.NRefactory.CSharp.Resolver;
using ICSharpCode.NRefactory.TypeSystem;
using MonoDevelop.TypeSystem;
using System.Collections.Generic;
using MonoDevelop.Projects;
using MonoDevelop.CSharp.Project;
using System.Linq;
using MonoDevelop.CSharp.Formatting;
using ICSharpCode.NRefactory.TypeSystem.Implementation;
using ICSharpCode.NRefactory.CSharp.Refactoring;
using System.Text;
using MonoDevelop.Ide.CodeTemplates;
using ICSharpCode.NRefactory.Semantics;
using ICSharpCode.NRefactory;
using ICSharpCode.NRefactory.Completion;
using ICSharpCode.NRefactory.CSharp.Completion;

namespace MonoDevelop.CSharp.Completion
{
	
	public class CSharpCompletionTextEditorExtension : CompletionTextEditorExtension, ICompletionDataFactory, IParameterCompletionDataFactory
	{
		internal Mono.TextEditor.TextEditorData textEditorData;
		internal ITypeResolveContext ctx;
		
		CompilationUnit unit;
		static readonly CompilationUnit emptyUnit = new CompilationUnit ();
		CompilationUnit Unit {
			get {
				return unit ?? emptyUnit;
			}
			set {
				unit = value;
			}
		}
		
		CSharpParsedFile CSharpParsedFile {
			get;
			set;
		}
		
		static bool EnableParameterInsight {
			get {
				return PropertyService.Get ("EnableParameterInsight", true);
			}
		}
		
		CSharpFormattingPolicy policy;
		public CSharpFormattingPolicy FormattingPolicy {
			get {
				if (policy == null) {
					IEnumerable<string> types = MonoDevelop.Ide.DesktopService.GetMimeTypeInheritanceChain (CSharpFormatter.MimeType);
					if (Document.Project != null && Document.Project.Policies != null) {
						policy = base.Document.Project.Policies.Get<CSharpFormattingPolicy> (types);
					} else {
						policy = MonoDevelop.Projects.Policies.PolicyService.GetDefaultPolicy<CSharpFormattingPolicy> (types);
					}
				}
				return policy;
			}
		}
		
		public CSharpCompletionTextEditorExtension ()
		{
		}
		
		/// <summary>
		/// Used in testing environment.
		/// </summary>
		[System.ComponentModel.Browsable(false)]
		public CSharpCompletionTextEditorExtension (MonoDevelop.Ide.Gui.Document doc) : this ()
		{
			Initialize (doc);
		}
		
		public override void Initialize ()
		{
			base.Initialize ();
			ctx = Document.TypeResolveContext;
			textEditorData = Document.Editor;
			var parsedDocument = document.ParsedDocument;
			if (parsedDocument != null) {
				this.Unit = parsedDocument.Annotation<CompilationUnit> ();
				this.CSharpParsedFile = parsedDocument.Annotation<CSharpParsedFile> ();
			}
			
			Document.DocumentParsed += delegate {
				var newDocument = Document.ParsedDocument;
				if (newDocument == null) 
					return;
				this.Unit = newDocument.Annotation<CompilationUnit> ();
				this.CSharpParsedFile = newDocument.Annotation<CSharpParsedFile> ();
				var textEditor = Editor.Parent;
				if (textEditor != null) {
					textEditor.TextViewMargin.PurgeLayoutCache ();
					textEditor.RedrawMarginLines (textEditor.TextViewMargin, 1, Editor.LineCount);
				}
			};
		}
		
		
		public override bool KeyPress (Gdk.Key key, char keyChar, Gdk.ModifierType modifier)
		{
			bool result = base.KeyPress (key, keyChar, modifier);
			
			if (EnableParameterInsight && (keyChar == ',' || keyChar == ')') && CanRunParameterCompletionCommand ())
				base.RunParameterCompletionCommand ();
			
//			if (IsInsideComment ())
//				ParameterInformationWindowManager.HideWindow (CompletionWidget);
			return result;
		}
		
		public override ICompletionDataList HandleCodeCompletion (CodeCompletionContext completionContext, char completionChar, ref int triggerWordLength)
		{
			//	var timer = Counters.ResolveTime.BeginTiming ();
			try {
				if (char.IsLetterOrDigit (completionChar))
					triggerWordLength = 1;
				return InternalHandleCodeCompletion (completionContext, completionChar, false, ref triggerWordLength);
			} catch (Exception e) {
				LoggingService.LogError ("Unexpected code completion exception." + Environment.NewLine + 
					"FileName: " + Document.FileName + Environment.NewLine + 
					"Position: line=" + completionContext.TriggerLine + " col=" + completionContext.TriggerLineOffset + Environment.NewLine + 
					"Line text: " + Document.Editor.GetLineText (completionContext.TriggerLine), 
					e);
				return null;
			} finally {
				//			if (timer != null)
				//				timer.Dispose ();
			}
		}
		
		ICompletionDataList InternalHandleCodeCompletion (CodeCompletionContext completionContext, char completionChar, bool ctrlSpace, ref int triggerWordLength)
		{
			if (!TextEditorProperties.EnableAutoCodeCompletion)
				return null;
			if (textEditorData.CurrentMode is CompletionTextLinkMode) {
				if (!((CompletionTextLinkMode)textEditorData.CurrentMode).TriggerCodeCompletion)
					return null;
			} else if (textEditorData.CurrentMode is Mono.TextEditor.TextLinkEditMode) {
				return null;
			}
			if (Unit == null || CSharpParsedFile == null)
				return null;
			var list = new CompletionDataList ();
			var engine = new CSharpCompletionEngine (textEditorData.Document, this);
			engine.ctx = ctx;
			engine.Unit = Unit;
			engine.CSharpParsedFile = CSharpParsedFile;
			engine.FormattingPolicy = FormattingPolicy.CreateOptions ();
			engine.ProjectContent = Document.GetProjectContext ();
			engine.EolMarker = textEditorData.EolMarker;
			engine.IndentString = textEditorData.Options.IndentationString;
			list.AddRange (engine.GetCompletionData (completionContext.TriggerOffset, ctrlSpace));
			list.AutoCompleteEmptyMatch = engine.AutoCompleteEmptyMatch;
			list.AutoSelect = engine.AutoSelect;
			list.DefaultCompletionString = engine.DefaultCompletionString;
			return list;
		}
		
		public override ICompletionDataList CodeCompletionCommand (CodeCompletionContext completionContext)
		{
			int triggerWordLength = 0;
			char ch = completionContext.TriggerOffset > 0 ? textEditorData.GetCharAt (completionContext.TriggerOffset - 1) : '\0';
			return InternalHandleCodeCompletion (completionContext, ch, true, ref triggerWordLength);
			
		}
		
		static bool ContainsPublicConstructors (ITypeDefinition t)
		{
			if (t.Methods.Count (m => m.IsConstructor) == 0)
				return true;
			return t.Methods.Any (m => m.IsConstructor && m.IsPublic);
		}


//			CompletionDataList result = new ProjectDomCompletionDataList ();
//			// "var o = new " needs special treatment.
//			if (returnType == null && returnTypeUnresolved != null && returnTypeUnresolved.FullName == "var")
//				returnType = returnTypeUnresolved = DomReturnType.Object;
//
//			//	ExpressionContext.TypeExpressionContext tce = context as ExpressionContext.TypeExpressionContext;
//
//			CompletionDataCollector col = new CompletionDataCollector (this, dom, result, Document.CompilationUnit, callingType, location);
//			IType type = null;
//			if (returnType != null)
//				type = dom.GetType (returnType);
//			if (type == null)
//				type = dom.SearchType (Document.CompilationUnit, callingType, location, returnTypeUnresolved);
//			
//			// special handling for nullable types: Bug 674516 - new completion for nullables should not include "Nullable"
//			if (type is InstantiatedType && ((InstantiatedType)type).UninstantiatedType.FullName == "System.Nullable" && ((InstantiatedType)type).GenericParameters.Count == 1) {
//				var genericParameter = ((InstantiatedType)type).GenericParameters [0];
//				returnType = returnTypeUnresolved = Document.CompilationUnit.ShortenTypeName (genericParameter, location);
//				type = dom.SearchType (Document.CompilationUnit, callingType, location, genericParameter);
//			}
//			
//			if (type == null || !(type.IsAbstract || type.ClassType == ClassType.Interface)) {
//				if (type == null || type.ConstructorCount == 0 || type.Methods.Any (c => c.IsConstructor && c.IsAccessibleFrom (dom, callingType, type, callingType != null && dom.GetInheritanceTree (callingType).Any (x => x.FullName == type.FullName)))) {
//					if (returnTypeUnresolved != null) {
//						col.FullyQualify = true;
//						CompletionData unresovedCompletionData = col.Add (returnTypeUnresolved);
//						col.FullyQualify = false;
//						// don't set default completion string for arrays, since it interferes with: 
//						// string[] arr = new string[] vs new { "a"}
//						if (returnTypeUnresolved.ArrayDimensions == 0)
//							result.DefaultCompletionString = StripGenerics (unresovedCompletionData.CompletionText);
//					} else {
//						CompletionData unresovedCompletionData = col.Add (returnType);
//						if (returnType.ArrayDimensions == 0)
//							result.DefaultCompletionString = StripGenerics (unresovedCompletionData.CompletionText);
//					}
//				}
//			}
//			
//			//				if (tce != null && tce.Type != null) {
//			//					result.DefaultCompletionString = StripGenerics (col.AddCompletionData (result, tce.Type).CompletionString);
//			//				} 
//			//			else {
//			//			}
//			if (type == null)
//				return result;
//			HashSet<string > usedNamespaces = new HashSet<string> (GetUsedNamespaces ());
//			if (type.FullName == DomReturnType.Object.FullName) 
//				AddPrimitiveTypes (col);
//			
//			foreach (IType curType in dom.GetSubclasses (type)) {
//				if (context != null && context.FilterEntry (curType))
//					continue;
//				if ((curType.TypeModifier & TypeModifier.HasOnlyHiddenConstructors) == TypeModifier.HasOnlyHiddenConstructors)
//					continue;
//				if (usedNamespaces.Contains (curType.Namespace)) {
//					if (curType.ConstructorCount > 0) {
//						if (!(curType.Methods.Any (c => c.IsConstructor && c.IsAccessibleFrom (dom, curType, callingType, callingType != null && dom.GetInheritanceTree (callingType).Any (x => x.FullName == curType.FullName)))))
//							continue;
//					}
//					col.Add (curType);
//				} else {
//					string nsName = curType.Namespace;
//					int idx = nsName.IndexOf ('.');
//					if (idx >= 0)
//						nsName = nsName.Substring (0, idx);
//					col.Add (new Namespace (nsName));
//				}
//			}
//			
//			// add aliases
//			if (returnType != null) {
//				foreach (IUsing u in Document.CompilationUnit.Usings) {
//					foreach (KeyValuePair<string, IReturnType> alias in u.Aliases) {
//						if (alias.Value.ToInvariantString () == returnType.ToInvariantString ())
//							result.Add (alias.Key, "md-class");
//					}
//				}
//			}
//			
//			return result;
//		}
		
//		IEnumerable<ICompletionData> GetDefineCompletionData ()
//		{
//			if (Document.Project == null)
//				yield break;
//			
//			var symbols = new Dictionary<string, string> ();
//			var cp = new ProjectDomCompletionDataList ();
//			foreach (DotNetProjectConfiguration conf in Document.Project.Configurations) {
//				var cparams = conf.CompilationParameters as CSharpCompilerParameters;
//				if (cparams != null) {
//					string[] syms = cparams.DefineSymbols.Split (';');
//					foreach (string s in syms) {
//						string ss = s.Trim ();
//						if (ss.Length > 0 && !symbols.ContainsKey (ss)) {
//							symbols [ss] = ss;
//							yield return factory.CreateLiteralCompletionData (ss);
//						}
//					}
//				}
//			}
//		}
		
		public override IParameterDataProvider HandleParameterCompletion (CodeCompletionContext completionContext, char completionChar)
		{
			if (!TextEditorProperties.EnableCodeCompletion)
				return null;
			if (Unit == null || CSharpParsedFile == null)
				return null;
			var engine = new CSharpParameterCompletionEngine (textEditorData.Document, this);
			engine.ctx = ctx;
			engine.Unit = Unit;
			engine.CSharpParsedFile = CSharpParsedFile;
			engine.ProjectContent = Document.GetProjectContext ();
			return engine.GetParameterDataProvider (completionContext.TriggerOffset);
		}
		
		List<string> GetUsedNamespaces ()
		{
			var scope = CSharpParsedFile.GetUsingScope (new TextLocation (document.Editor.Caret.Line, document.Editor.Caret.Column));
			var result = new List<string> ();
			while (scope != null) {
				result.Add (scope.NamespaceName);
				foreach (var u in scope.Usings) {
					var ns = u.ResolveNamespace (ctx);
					if (ns == null)
						continue;
					result.Add (ns.NamespaceName);
				}
				scope = scope.Parent;
			}
			return result;
		}
		/*
		public override bool GetParameterCompletionCommandOffset (out int cpos)
		{
			// Start calculating the parameter offset from the beginning of the
			// current member, instead of the beginning of the file. 
			cpos = textEditorData.Caret.Offset - 1;
			var parsedDocument = Document.ParsedDocument;
			if (parsedDocument == null)
				return false;
			IMember mem = currentMember;
			if (mem == null || (mem is IType))
				return false;
			int startPos = textEditorData.LocationToOffset (mem.Region.BeginLine, mem.Region.BeginColumn);
			int parenDepth = 0;
			int chevronDepth = 0;
			while (cpos > startPos) {
				char c = textEditorData.GetCharAt (cpos);
				if (c == ')')
					parenDepth++;
				if (c == '>')
					chevronDepth++;
				if (parenDepth == 0 && c == '(' || chevronDepth == 0 && c == '<') {
					int p = MethodParameterDataProvider.GetCurrentParameterIndex (CompletionWidget, cpos + 1, startPos);
					if (p != -1) {
						cpos++;
						return true;
					} else {
						return false;
					}
				}
				if (c == '(')
					parenDepth--;
				if (c == '<')
					chevronDepth--;
				cpos--;
			}
			return false;
		}*/

		public override int GetCurrentParameterIndex (CodeCompletionContext ctx)
		{
			return GetCurrentParameterIndex (CompletionWidget, ctx.TriggerOffset, 0);
		}
		
		internal static int GetCurrentParameterIndex (ICompletionWidget widget, int offset, int memberStart)
		{
			int cursor = widget.CurrentCodeCompletionContext.TriggerOffset;
			int i = offset;
			
			if (i > cursor)
				return -1;
			if (i == cursor) 
				return 1; // parameters are 1 based
			IEnumerable<string> types = MonoDevelop.Ide.DesktopService.GetMimeTypeInheritanceChain (CSharpFormatter.MimeType);
			CSharpIndentEngine engine = new CSharpIndentEngine (MonoDevelop.Projects.Policies.PolicyService.GetDefaultPolicy<CSharpFormattingPolicy> (types));
			int index = memberStart + 1;
			int parentheses = 0;
			int bracket = 0;
			do {
				char c = widget.GetChar (i - 1);
				engine.Push (c);
				switch (c) {
				case '{':
					if (!engine.IsInsideOrdinaryCommentOrString)
						bracket++;
					break;
				case '}':
					if (!engine.IsInsideOrdinaryCommentOrString)
						bracket--;
					break;
				case '(':
					if (!engine.IsInsideOrdinaryCommentOrString)
						parentheses++;
					break;
				case ')':
					if (!engine.IsInsideOrdinaryCommentOrString)
						parentheses--;
					break;
				case ',':
					if (!engine.IsInsideOrdinaryCommentOrString && parentheses == 1 && bracket == 0)
						index++;
					break;
				}
				i++;
			} while (i <= cursor && parentheses >= 0);
			
			return parentheses != 1 || bracket > 0 ? -1 : index;
		}

		#region ICompletionDataFactory implementation
		ICompletionData ICompletionDataFactory.CreateEntityCompletionData (IEntity entity)
		{
			return new MemberCompletionData (this, entity as IMember, OutputFlags.IncludeGenerics | OutputFlags.HideArrayBrackets | OutputFlags.IncludeParameterName) {
				HideExtensionParameter = true
			};
		}

		ICompletionData ICompletionDataFactory.CreateEntityCompletionData (IEntity entity, string text)
		{
			return new CompletionData (text, entity.GetStockIcon (), null, text);
		}

		ICompletionData ICompletionDataFactory.CreateTypeCompletionData (IType type, string shortType)
		{
			return new CompletionData (shortType, type.GetStockIcon ());
		}
		
		ICompletionData ICompletionDataFactory.CreateLiteralCompletionData (string title, string description, string insertText)
		{
			return new CompletionData (title, "md-keyword", description, insertText ?? title);
		}

		ICompletionData ICompletionDataFactory.CreateNamespaceCompletionData (string name)
		{
			return new CompletionData (name, Stock.Namespace);
		}

		ICompletionData ICompletionDataFactory.CreateVariableCompletionData (IVariable variable)
		{
			return new VariableCompletionData (variable);
		}

		ICompletionData ICompletionDataFactory.CreateVariableCompletionData (ITypeParameter parameter)
		{
			return new CompletionData (parameter.Name, parameter.GetStockIcon ());
		}

		ICompletionData ICompletionDataFactory.CreateEventCreationCompletionData (string varName, IType delegateType, IEvent evt, string parameterDefinition, IMember currentMember, ITypeDefinition currentType)
		{
			return new EventCreationCompletionData (this, varName, delegateType, evt, parameterDefinition, currentMember, currentType);
		}
		
		ICompletionData ICompletionDataFactory.CreateNewOverrideCompletionData (int declarationBegin, ITypeDefinition type, IMember m)
		{
			return new NewOverrideCompletionData (this, declarationBegin, type, m);
		}
		
		IEnumerable<ICompletionData> ICompletionDataFactory.CreateCodeTemplateCompletionData ()
		{
			var result = new CompletionDataList ();
			CodeTemplateService.AddCompletionDataForMime ("text/x-csharp", result);
			return result;
		}
		
		IEnumerable<ICompletionData> ICompletionDataFactory.CreatePreProcessorDefinesCompletionData ()
		{
			var project = document.Project;
			if (project == null)
				yield break;
			var configuration = project.GetConfiguration (MonoDevelop.Ide.IdeApp.Workspace.ActiveConfiguration) as DotNetProjectConfiguration;
			var par = configuration != null ? configuration.CompilationParameters as CSharpCompilerParameters : null;
			if (par == null)
				yield break;
			foreach (var define in par.DefineSymbols.Split (';', ',', ' ', '\t').Where (s => !string.IsNullOrWhiteSpace (s)))
				yield return new CompletionData (define, "md-keyword");
				
		}
		#endregion

		#region IParameterCompletionDataFactory implementation
		IParameterDataProvider IParameterCompletionDataFactory.CreateConstructorProvider (IType type)
		{
			return new ConstructorParameterDataProvider (this, type);
		}

		IParameterDataProvider IParameterCompletionDataFactory.CreateMethodDataProvider (MethodGroupResolveResult par1)
		{
			return new MethodParameterDataProvider (this, par1);
		}

		public IParameterDataProvider CreateMethodDataProvider (IMethod method)
		{
			return new MethodParameterDataProvider (this, method);
		}

		IParameterDataProvider IParameterCompletionDataFactory.CreateDelegateDataProvider (IType type)
		{
			return new DelegateDataProvider (this, type);
		}
		
		IParameterDataProvider IParameterCompletionDataFactory.CreateIndexerParameterDataProvider (IType type, AstNode resolvedNode)
		{
			return new IndexerParameterDataProvider (this, type, resolvedNode);
		}
		#endregion
	}
}
