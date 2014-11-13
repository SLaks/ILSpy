using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using ICSharpCode.Decompiler.ILAst;
using ICSharpCode.NRefactory.CSharp;
using Mono.Cecil;

namespace ICSharpCode.Decompiler.Ast.Transforms
{
	/// <summary>
	/// The Roslyn compiler likes to insert extra durable temporary variables.
	/// This transform inlines any variable that is assigned to another variable, 
	/// as long as neither variable is ever assigned afterwards.
	/// 
	/// This must run before lambda transforms.  In order for this to run after
	/// lambda transforms, it must track the enclosing lambda containing each
	/// variable (use the generic data parameter), and set IsReassigned on any 
	/// assignment not in the enclosing lambda
	/// </summary>
	class InlineTemporaries : DepthFirstAstVisitor<object, object>, IAstTransform
	{
		readonly Dictionary<ILVariable, VariableInfo> variables = new Dictionary<ILVariable, VariableInfo>();
		class VariableInfo
		{
			public ILVariable Variable { get; set; }
			///<summary>The value expression that this variable is first assigned to.</summary>
			public Expression FirstAssignment { get; set; }
			///<summary>True if the variable is ever re-assigned, in which case it cannot participate in inlining.</summary>
			public bool IsReassigned { get; set; }

			///<summary>Every reference to this variable.</summary>
			public readonly List<IdentifierExpression> Usages = new List<IdentifierExpression>();
		}

		public void Run(AstNode compilationUnit)
		{
			compilationUnit.AcceptVisitor(this, null);

			// Maps variables that we have already inlined to their replacements.
			// This is used in case we inline a chain out of order.  For example:
			//   a = ..;
			//   b = a;
			//   c = b;
			// If we replace all occurrences of b with a before replacing c, this
			// map is used to make sure that we replace c with a rather than b.
			var inlinedVariables = new Dictionary<ILVariable, VariableInfo>(variables.Count);
			foreach (var extraVariable in variables.Values) {
				if (extraVariable.IsReassigned
				 || (extraVariable.Variable.Type.IsValueType && !extraVariable.Variable.Type.IsPrimitive)
				 || !(extraVariable.FirstAssignment is IdentifierExpression))
					continue;
				var targetVariable = extraVariable.FirstAssignment.Annotation<ILVariable>();

				VariableInfo targetInfo;
				if (!inlinedVariables.TryGetValue(targetVariable, out targetInfo))
					targetInfo = variables[targetVariable];
				if (targetInfo.IsReassigned)
					continue;
				inlinedVariables[extraVariable.Variable] = targetInfo;

				extraVariable.Usages.ForEach(u => ReplaceVariable(u, targetVariable));

				// Now, remove the assignment statement.  
				// FirstAssignment is the right half of the VariableInitializer or the AssignmentExpression.
				// Thus, its parent is either an AssignmentExpression or a VariableInitializer.  ILSpy won't
				// ever generate a variable declaration statement with two variables.
				var assignmentExpr = extraVariable.FirstAssignment.Parent;
				if (assignmentExpr.Parent is ExpressionStatement || assignmentExpr.Parent is VariableDeclarationStatement)
					assignmentExpr.Parent.Remove();
				else
					assignmentExpr.ReplaceWith(extraVariable.FirstAssignment);
			}
		}

		static void ReplaceVariable(IdentifierExpression expression, ILVariable target)
		{
			expression.Identifier = target.Name;
			expression.RemoveAnnotations<ILVariable>();
			expression.AddAnnotation(target);
		}
		public override object VisitVariableInitializer(VariableInitializer variableInitializer, object data)
		{
			if (!(variableInitializer.Parent is VariableDeclarationStatement))
				return base.VisitVariableInitializer(variableInitializer, data);
			var ilv = variableInitializer.Annotation<ILVariable>();
			variables.Add(ilv, new VariableInfo { Variable = ilv, FirstAssignment = variableInitializer.Initializer });
			return base.VisitVariableInitializer(variableInitializer, data);
		}
		public override object VisitForeachStatement(ForeachStatement foreachStatement, object data)
		{
			var ilv = foreachStatement.Annotation<ILVariable>();
			// Foreach variables cannot be inlined, but other variables
			// can be inlined into them if they are never reassigned.
			variables.Add(ilv, new VariableInfo { Variable = ilv, FirstAssignment = Expression.Null });
			return base.VisitForeachStatement(foreachStatement, data);
		}
		public override object VisitCatchClause(CatchClause catchClause, object data)
		{
			var ilv = catchClause.Annotation<ILVariable>();	// Will be null for catch (Exception)
															// Catch variables cannot be inlined, but other variables
															// can be inlined into them if they are never reassigned.
			if (ilv != null)
				variables.Add(ilv, new VariableInfo { Variable = ilv, FirstAssignment = Expression.Null });
			return base.VisitCatchClause(catchClause, data);
		}
		public override object VisitAssignmentExpression(AssignmentExpression assignmentExpression, object data)
		{
			if (!(assignmentExpression.Left is IdentifierExpression))
				return base.VisitAssignmentExpression(assignmentExpression, data);
			var variable = TryGetVariable(assignmentExpression.Left as IdentifierExpression);
			if (variable == null)
				return base.VisitAssignmentExpression(assignmentExpression, data);
			if (variable.FirstAssignment == null)
				variable.FirstAssignment = assignmentExpression.Right;
			else
				variable.IsReassigned = true;
			return base.VisitAssignmentExpression(assignmentExpression, data);
		}

		public override object VisitDirectionExpression(DirectionExpression directionExpression, object data)
		{
			var variable = TryGetVariable(directionExpression.Expression as IdentifierExpression);
			if (variable != null)
				variable.IsReassigned = true;
			return base.VisitDirectionExpression(directionExpression, data);
		}

		public override object VisitIdentifierExpression(IdentifierExpression identifierExpression, object data)
		{
			var variable = TryGetVariable(identifierExpression);
			if (variable != null)
				variable.Usages.Add(identifierExpression);
			return base.VisitIdentifierExpression(identifierExpression, data);
		}

		VariableInfo TryGetVariable(IdentifierExpression expression)
		{
			if (expression == null)
				return null;
			var ilv = expression.Annotation<ILVariable>();
			if (ilv == null)
				return null;

			// If the variable is a parameter we've never encountered before, add it.
			VariableInfo retVal;
			if (!variables.TryGetValue(ilv, out retVal)) {
				Debug.Assert(ilv.IsParameter);
				// Parameters are already initialized, but have no expression that they were
				// initialized to. I set FirstAssignment to an Expression instance so we can
				// set IsReassigned if it is ever assigned again.
				retVal = new VariableInfo {
					Variable = ilv,
					FirstAssignment = Expression.Null,
					IsReassigned = ilv.Type is ByReferenceType
				};
				variables.Add(ilv, retVal);
			}
			return retVal;
		}
	}
}
