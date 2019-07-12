using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using XamlX.Ast;
using XamlX.Transform.Emitters;
using XamlX.Transform.Transformers;
using XamlX.TypeSystem;

namespace XamlX.Transform
{
#if !XAMLIL_INTERNAL
    public
#endif
    class XamlXCompiler
    {
        private readonly XamlXTransformerConfiguration _configuration;
        public List<IXamlXAstTransformer> Transformers { get; } = new List<IXamlXAstTransformer>();
        public List<IXamlXAstTransformer> SimplificationTransformers { get; } = new List<IXamlXAstTransformer>();
        public List<IXamlXAstNodeEmitter> Emitters { get; } = new List<IXamlXAstNodeEmitter>();
        public XamlXCompiler(XamlXTransformerConfiguration configuration, bool fillWithDefaults)
        {
            _configuration = configuration;
            if (fillWithDefaults)
            {
                Transformers = new List<IXamlXAstTransformer>
                {
                    new XamlXKnownDirectivesTransformer(),
                    new XamlXIntrinsicsTransformer(),
                    new XamlXXArgumentsTransformer(),
                    new XamlXTypeReferenceResolver(),
                    new XamlXMarkupExtensionTransformer(),
                    new XamlXPropertyReferenceResolver(),
                    new XamlXContentConvertTransformer(),
                    new XamlXResolveContentPropertyTransformer(),
                    new XamlXResolvePropertyValueAddersTransformer(),
                    new XamlXConvertPropertyValuesToAssignmentsTransformer(),
                    new XamlXNewObjectTransformer(),
                    new XamlXDeferredContentTransformer(),
                    new XamlXTopDownInitializationTransformer(),
                };
                SimplificationTransformers = new List<IXamlXAstTransformer>
                {
                    new XamlXFlattenTransformer()
                };
                Emitters = new List<IXamlXAstNodeEmitter>()
                {
                    new NewObjectEmitter(),
                    new TextNodeEmitter(),
                    new MethodCallEmitter(),
                    new PropertyAssignmentEmitter(),
                    new PropertyValueManipulationEmitter(),
                    new ManipulationGroupEmitter(),
                    new ValueWithManipulationsEmitter(),
                    new MarkupExtensionEmitter(),
                    new ObjectInitializationNodeEmitter()
                };
            }
        }

        public XamlXAstTransformationContext CreateTransformationContext(XamlXDocument doc, bool strict)
            => new XamlXAstTransformationContext(_configuration, doc.NamespaceAliases, strict);
        
        public void Transform(XamlXDocument doc,bool strict = true)
        {
            var ctx = CreateTransformationContext(doc, strict);

            var root = doc.Root;
            ctx.RootObject = new XamlXRootObjectNode((XamlXAstObjectNode)root);
            foreach (var transformer in Transformers)
            {
                ctx.VisitChildren(ctx.RootObject, transformer);
                root = ctx.Visit(root, transformer);
            }

            foreach (var simplifier in SimplificationTransformers)
                root = ctx.Visit(root, simplifier);

            doc.Root = root;
        }

        XamlXEmitContext InitCodeGen(
            IFileSource file,
            Func<string, IXamlXType, IXamlXTypeBuilder> createSubType,
            IXamlXEmitter codeGen, XamlXContext context, bool needContextLocal)
        {
            IXamlXLocal contextLocal = null;

            if (needContextLocal)
            {
                contextLocal = codeGen.DefineLocal(context.ContextType);
                // Pass IService provider as the first argument to context factory
                codeGen
                    .Emit(OpCodes.Ldarg_0);
                context.Factory(codeGen);
                codeGen.Emit(OpCodes.Stloc, contextLocal);
            }

            var emitContext = new XamlXEmitContext(codeGen, _configuration, context, contextLocal, createSubType, file, Emitters);
            return emitContext;
        }
        
        void CompileBuild(
            IFileSource fileSource,
            IXamlXAstValueNode rootInstance, Func<string, IXamlXType, IXamlXTypeBuilder> createSubType,
            IXamlXEmitter codeGen, XamlXContext context, IXamlXMethod compiledPopulate)
        {
            var needContextLocal = !(rootInstance is XamlXAstNewClrObjectNode newObj && newObj.Arguments.Count == 0);
            var emitContext = InitCodeGen(fileSource, createSubType, codeGen, context, needContextLocal);


            var rv = codeGen.DefineLocal(rootInstance.Type.GetClrType());
            emitContext.Emit(rootInstance, codeGen, rootInstance.Type.GetClrType());
            codeGen
                .Emit(OpCodes.Stloc, rv)
                .Emit(OpCodes.Ldarg_0)
                .Emit(OpCodes.Ldloc, rv)
                .EmitCall(compiledPopulate)
                .Emit(OpCodes.Ldloc, rv)
                .Emit(OpCodes.Ret);
        }

        /// <summary>
        /// void Populate(IServiceProvider sp, T target);
        /// </summary>

        void CompilePopulate(IFileSource fileSource, IXamlXAstManipulationNode manipulation, Func<string, IXamlXType, IXamlXTypeBuilder> createSubType, IXamlXEmitter codeGen, XamlXContext context)
        {
            // Uncomment to inspect generated IL in debugger
            //codeGen = new RecordingIlEmitter(codeGen);
            var emitContext = InitCodeGen(fileSource, createSubType, codeGen, context, true);

            codeGen
                .Emit(OpCodes.Ldloc, emitContext.ContextLocal)
                .Emit(OpCodes.Ldarg_1)
                .Emit(OpCodes.Stfld, context.RootObjectField)
                .Emit(OpCodes.Ldloc, emitContext.ContextLocal)
                .Emit(OpCodes.Ldarg_1)
                .Emit(OpCodes.Stfld, context.IntermediateRootObjectField)
                .Emit(OpCodes.Ldarg_1);
            emitContext.Emit(manipulation, codeGen, null);
            codeGen.Emit(OpCodes.Ret);
        }

        public IXamlXType CreateContextType(IXamlXTypeBuilder builder)
        {
            return XamlXContextDefinition.GenerateContextClass(builder,
                _configuration.TypeSystem,
                _configuration.TypeMappings);
        }

        public IXamlXMethodBuilder DefinePopulateMethod(IXamlXTypeBuilder typeBuilder,
            XamlXDocument doc,
            string name, bool isPublic)
        {
            var rootGrp = (XamlXValueWithManipulationNode) doc.Root;
            return typeBuilder.DefineMethod(_configuration.WellKnownTypes.Void,
                new[] {_configuration.TypeMappings.ServiceProvider, rootGrp.Type.GetClrType()},
                name, isPublic, true, false);
        }

        public IXamlXMethodBuilder DefineBuildMethod(IXamlXTypeBuilder typeBuilder,
            XamlXDocument doc,
            string name, bool isPublic)
        {
            var rootGrp = (XamlXValueWithManipulationNode) doc.Root;
            return typeBuilder.DefineMethod(rootGrp.Type.GetClrType(),
                new[] {_configuration.TypeMappings.ServiceProvider}, name, isPublic, true, false);
        }
        
        public void Compile(XamlXDocument doc, IXamlXTypeBuilder typeBuilder, IXamlXType contextType,
            string populateMethodName, string createMethodName, string namespaceInfoClassName,
            string baseUri, IFileSource fileSource)
        {
            var rootGrp = (XamlXValueWithManipulationNode) doc.Root;
            Compile(doc, contextType,
                DefinePopulateMethod(typeBuilder, doc, populateMethodName, true),
                createMethodName == null ?
                    null :
                    DefineBuildMethod(typeBuilder, doc, createMethodName, true),
                _configuration.TypeMappings.XmlNamespaceInfoProvider == null ?
                    null :
                    typeBuilder.DefineSubType(_configuration.WellKnownTypes.Object,
                        namespaceInfoClassName, false),
                (name, bt) => typeBuilder.DefineSubType(bt, name, false),
                baseUri, fileSource);

        }
        
        public void Compile( XamlXDocument doc, IXamlXType contextType,
            IXamlXMethodBuilder populateMethod, IXamlXMethodBuilder buildMethod,
            IXamlXTypeBuilder namespaceInfoBuilder,
            Func<string, IXamlXType, IXamlXTypeBuilder> createClosure,
            string baseUri, IFileSource fileSource)
        {
            var rootGrp = (XamlXValueWithManipulationNode) doc.Root;
            var staticProviders = new List<IXamlXField>();

            if (namespaceInfoBuilder != null)
            {

                staticProviders.Add(
                    XamlXNamespaceInfoHelper.EmitNamespaceInfoProvider(_configuration, namespaceInfoBuilder, doc));
            }
            
            var context = new XamlXContext(contextType, rootGrp.Type.GetClrType(),
                _configuration.TypeMappings, baseUri, staticProviders);
            
            CompilePopulate(fileSource, rootGrp.Manipulation, createClosure, populateMethod.Generator, context);

            if (buildMethod != null)
            {
                CompileBuild(fileSource, rootGrp.Value, null, buildMethod.Generator, context, populateMethod);
            }

            namespaceInfoBuilder?.CreateType();
        }
        
        
        
    }


#if !XAMLIL_INTERNAL
    public
#endif
    interface IXamlXAstTransformer
    {
        IXamlXAstNode Transform(XamlXAstTransformationContext context, IXamlXAstNode node);
    }

#if !XAMLIL_INTERNAL
    public
#endif
    class XamlXNodeEmitResult
    {
        public int ConsumedItems { get; }
        public IXamlXType ReturnType { get; set; }
        public int ProducedItems => ReturnType == null ? 0 : 1;
        public bool AllowCast { get; set; }

        public XamlXNodeEmitResult(int consumedItems, IXamlXType returnType = null)
        {
            ConsumedItems = consumedItems;
            ReturnType = returnType;
        }

        public static XamlXNodeEmitResult Void(int consumedItems) => new XamlXNodeEmitResult(consumedItems);

        public static XamlXNodeEmitResult Type(int consumedItems, IXamlXType type) =>
            new XamlXNodeEmitResult(consumedItems, type);
    }
    
#if !XAMLIL_INTERNAL
    public
#endif
    interface IXamlXAstNodeEmitter
    {
        XamlXNodeEmitResult Emit(IXamlXAstNode node, XamlXEmitContext context, IXamlXEmitter codeGen);
    }

#if !XAMLIL_INTERNAL
    public
#endif
    interface IXamlXAstEmitableNode
    {
        XamlXNodeEmitResult Emit(XamlXEmitContext context, IXamlXEmitter codeGen);
    }
    
}
