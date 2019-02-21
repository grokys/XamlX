using System.Collections.Generic;
using System.Linq;
using XamlX.Ast;
using XamlX.TypeSystem;

namespace XamlX.Transform.Transformers
{
    public class XamlXPropertyReferenceResolver : IXamlXAstTransformer
    {
        public IXamlXAstNode Transform(XamlXAstTransformationContext context, IXamlXAstNode node)
        {
            if (node is XamlXAstNamePropertyReference prop)
            {
                if (!(prop.DeclaringType is XamlXAstClrTypeReference declaringRef))
                {
                    if (context.StrictMode)
                        throw new XamlXParseException(
                            $"Unable to resolve property {prop.Name} on {prop.DeclaringType}", node);
                    else
                        return node;
                }

                if (!(prop.TargetType is XamlXAstClrTypeReference targetRef))
                {
                    if (context.StrictMode)
                        throw new XamlXParseException($"Unable to resolve property on {prop.DeclaringType}", node);
                    else
                        return node;
                }

                var targetType = targetRef.Type;
                var declaringType = declaringRef.Type;

                // Can set normal properties of ancestor types and self
                if (declaringType.IsAssignableFrom(targetType))
                {
                    var found = declaringType.GetAllProperties().FirstOrDefault(p =>
                        p.Name == prop.Name
                        && ((p.Getter != null && !p.Getter.IsStatic && p.Getter.Parameters.Count == 0)
                            || p.Setter != null && !p.Setter.IsStatic && p.Setter.Parameters.Count == 1));
                    if (found != null)
                        return new XamlXAstClrPropertyReference(prop, found);
                    var clrEvent = declaringType.GetAllEvents().FirstOrDefault(p => p.Name == prop.Name
                                                                                    && p.Add != null);
                    if (clrEvent != null)
                        return new XamlXAstClrPropertyReference(prop,
                            new XamlXAstCustomProperty(prop.Name, clrEvent.Add, null));
                }

                // Look for attached properties on declaring type
                IXamlXMethod setter = null, getter = null, adder = null;
                var setterName = "Set" + prop.Name;
                var getterName = "Get" + prop.Name;
                var adderName = "Add" + prop.Name + "Handler";
                foreach (var m in declaringType.Methods)
                {
                    if (m.IsPublic && m.IsStatic)
                    {
                        if (m.Name == getterName && m.Parameters.Count == 1 &&
                            m.Parameters[0].IsAssignableFrom(targetType))
                            getter = m;

                        if (m.Name == setterName && m.Parameters.Count == 2 &&
                            m.Parameters[0].IsAssignableFrom(targetType))
                            setter = m;

                        if (m.Name == adderName
                            && m.Parameters.Count == 2
                            && m.Parameters[0].IsAssignableFrom(targetType))
                            adder = m;
                    }
                }

                if (setter != null || getter != null)
                    return new XamlXAstClrPropertyReference(prop, new XamlXAstAttachedProperty(prop.Name, setter, getter));

                if (adder != null)
                    return new XamlXAstClrPropertyReference(prop, new XamlXAstCustomProperty(prop.Name, adder, null));

                if (context.StrictMode)
                    throw new XamlXParseException(
                        $"Unable to resolve suitable regular or attached property {prop.Name} on type {declaringType.GetFqn()}",
                        node);
                return null;
            }

            return node;
        }
    }

    class XamlXAstAttachedProperty : XamlXAstCustomProperty
    {
        public XamlXAstAttachedProperty(string name, IXamlXMethod setter, IXamlXMethod getter) : base(name, setter, getter)
        {
        }
    }
    
    class XamlXAstCustomProperty : IXamlXProperty
    {
        public bool Equals(IXamlXProperty other)
        {
            if (other == null)
                return false;
            return other.Name == Name
                   && other.Getter.Equals(Getter)
                   && other.Setter.Equals(Setter);
        }

        public string Name { get; }
        public IXamlXType PropertyType { get; }
        public IXamlXMethod Setter { get; }
        public IXamlXMethod Getter { get; }
        public IReadOnlyList<IXamlXCustomAttribute> CustomAttributes { get; set; } = new IXamlXCustomAttribute[0];

        public XamlXAstCustomProperty(string name, IXamlXMethod setter, IXamlXMethod getter)
        {
            Name = name;
            Setter = setter;
            Getter = getter;
            PropertyType = getter != null ? getter.ReturnType : setter.Parameters.Last();
        }
    }

}