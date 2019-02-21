using System.Collections.Generic;
using System.Linq;
using XamlX.Ast;
using XamlX.TypeSystem;

namespace XamlX.Transform.Transformers
{
    public class XamlTopDownInitializationTransformer : IXamlAstTransformer
    {
        public IXamlAstNode Transform(XamlAstTransformationContext context, IXamlAstNode node)
        {
            var usableAttrs = context.Configuration.TypeMappings.UsableDuringInitializationAttributes;
            if (!(usableAttrs?.Count > 0))
                return node;
            bool UsableDuringInitialization(IXamlType type)
            {
                foreach (var attr in type.CustomAttributes)
                {
                    foreach (var attrType in usableAttrs)
                    {
                        if (attr.Type.Equals(attrType))
                            return attr.Parameters.Count == 0 || attr.Parameters[0] as bool? == true;
                    }
                }

                if (type.BaseType != null)
                    return UsableDuringInitialization(type.BaseType);
                return false;
            }

            bool TryConvert(
                IXamlAstValueNode checkedNode, out IXamlAstValueNode value, out IXamlAstManipulationNode deferred)
            {
                value = null;
                deferred = null;
                if (!(checkedNode is XamlValueWithManipulationNode manipulation
                      && manipulation.Manipulation is XamlObjectInitializationNode initializer
                      && UsableDuringInitialization(manipulation.Value.Type.GetClrType())))
                    return false;
                var local = new XamlAstCompilerLocalNode(manipulation.Value, manipulation.Value.Type.GetClrType());
                value = new XamlAstLocalInitializationNodeEmitter(local, manipulation.Value, local);
                deferred = new XamlAstManipulationImperativeNode(initializer,
                    new XamlAstImperativeValueManipulation(initializer, local, initializer));
                return true;
            }
            
            if (node is XamlPropertyAssignmentNode assignment)
            {
                if (!TryConvert(assignment.Value, out var nvalue, out var deferred))
                    return node;
                return new XamlManipulationGroupNode(assignment)
                {
                    Children =
                    {
                        new XamlPropertyAssignmentNode(assignment, assignment.Property, nvalue),
                        deferred
                    }
                };
            }
            else if (node is XamlNoReturnMethodCallNode call)
            {
                var deferredNodes = new List<IXamlAstManipulationNode>();
                for (var c = 0; c < call.Arguments.Count; c++)
                {
                    var arg = call.Arguments[c];
                    if (TryConvert(arg, out var narg, out var deferred))
                    {
                        call.Arguments[c] = narg;
                        deferredNodes.Add(deferred);
                    }
                }

                if (deferredNodes.Count != 0)
                {
                    var grp = new XamlManipulationGroupNode(call);
                    grp.Children.Add(call);
                    grp.Children.AddRange(deferredNodes);
                    return grp;
                }
            }

            return node;
        }
    }
}