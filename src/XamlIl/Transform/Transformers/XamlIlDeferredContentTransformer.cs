using System.Linq;
using XamlIl.Ast;

namespace XamlIl.Transform.Transformers
{
    public class XamlIlDeferredContentTransformer : IXamlIlAstTransformer
    {
        public IXamlIlAstNode Transform(XamlIlAstTransformationContext context, IXamlIlAstNode node)
        {
            if (!(node is XamlIlPropertyAssignmentNode pa))
                return node;
            var deferredAttrs = context.Configuration.TypeMappings.DeferredContentPropertyAttributes;
            if (deferredAttrs.Count == 0)
                return node;
            if (!pa.Property.CustomAttributes.Any(ca => deferredAttrs.Any(da => da.Equals(ca.Type))))
                return node;
            
            pa.Value = new XamlIlDeferredContentNode(pa.Value, context.Configuration);
            return node;
        }
    }
}