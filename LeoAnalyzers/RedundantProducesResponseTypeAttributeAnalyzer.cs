using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.AspNetCore.Mvc;

namespace LeoAnalyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class RedundantProducesResponseTypeAttributeAnalyzer : DiagnosticAnalyzer
{
    private const string Message = "Redundant ProducesResponseType attribute";

    public static readonly DiagnosticDescriptor Rule = new(
        Rules.RedundantProducesResponseTypeAttribute,
        Message,
        Message,
        Rules.Categories.Design,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public record struct ReturnType(ITypeSymbol Type, ITypeSymbol? ObjectType);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

        context.RegisterCompilationStartAction(compilationContext =>
        {
            compilationContext.RegisterOperationAction(AnalyzeMethod, OperationKind.MethodBody);
        });
    }

    private static void AnalyzeMethod(OperationAnalysisContext context)
    {
        var methodOperation = (IMethodBodyOperation)context.Operation;

        if (methodOperation.SemanticModel?.GetDeclaredSymbol(methodOperation.Syntax) is not IMethodSymbol methodSymbol)
        {
            return;
        }

        var attributes = methodSymbol.GetAttributes();

        var producesResponseTypeAttributes = methodSymbol.GetAttributes()
            .Where(attr => attr.AttributeClass?.Name == "ProducesResponseTypeAttribute" &&
                           attr.AttributeClass.ContainingNamespace.ToDisplayString() == "Microsoft.AspNetCore.Mvc")
            .ToList();

        if (!producesResponseTypeAttributes.Any())
        {
            return;
        }

        var returnTypes = methodSymbol.DeclaringSyntaxReferences
            .SelectMany(syntaxRef => syntaxRef.GetSyntax(context.CancellationToken).DescendantNodes())
            .OfType<ReturnStatementSyntax>()
            .Where(returnStmt => returnStmt.Expression != null)
            .Select(returnStmt => new ReturnType(methodOperation.SemanticModel.GetTypeInfo(returnStmt.Expression!).Type!, GetObjectType(returnStmt, methodOperation.SemanticModel)))
            .Distinct(new ReturnTypeEqualityComparer())
            .ToList();

        foreach (var attribute in producesResponseTypeAttributes)
        {
            if (attribute.ConstructorArguments.Length == 0)
            {
                continue;
            }

            var statusCodeType = attribute.ConstructorArguments
                .FirstOrDefault(a => a.Type?.SpecialType == SpecialType.System_Int32 && a.Value is int);
            var objectType = attribute.ConstructorArguments
                .FirstOrDefault(a => a.Type?.Name == "Type" && a.Value is INamedTypeSymbol);

            var statusCode = (int)statusCodeType.Value!;

            if (!IsAnyMatchingResponse(returnTypes, attribute))
            {
                var diagnostic = Diagnostic.Create(
                    Rule,
                    attribute.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation(),
                    attribute.AttributeClass?.Name,
                    statusCode);

                context.ReportDiagnostic(diagnostic);
            }
        }
    }

    public static ITypeSymbol? GetObjectType(ReturnStatementSyntax returnStatement, SemanticModel semanticModel)
    {
        if (returnStatement.Expression is not InvocationExpressionSyntax invocation) return null;
        if (semanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol methodSymbol) return null;
        // The first argument of Ok(...)
        if (invocation.ArgumentList.Arguments.Count <= 0) return null;
        var firstArgument = invocation.ArgumentList.Arguments[0].Expression;
        var argumentType = semanticModel.GetTypeInfo(firstArgument).Type;

        return argumentType ?? null;
    }

    public static bool IsAnyMatchingResponse(List<ReturnType> returnTypes, AttributeData attribute)
    {
        var statusCodeType = attribute.ConstructorArguments
            .FirstOrDefault(a => a.Type?.SpecialType == SpecialType.System_Int32 && a.Value is int);


        var statusCode = (int)statusCodeType.Value!;

        return returnTypes.Any(returnType =>
        {
            var type = returnType.Type;
            var isMatchingStatusCode = IsMatchingStatusCode(type, statusCodeType);
            var isMatchingStatusCode2 = IsMatchingStatusCode(type, statusCode);
            var isMatchingObjectType = true;
            if (attribute.AttributeConstructor?.Parameters.Length == 2)
            {
                var objectType = attribute.ConstructorArguments
                    .FirstOrDefault(a => a is { Kind: TypedConstantKind.Type, Value: INamedTypeSymbol });
                isMatchingObjectType = IsMatchingResponseObject(returnType, objectType);
            }

            return isMatchingStatusCode && isMatchingStatusCode2 && isMatchingObjectType;
        });

    }

    public static bool IsMatchingResponseObject(ReturnType? returnType, TypedConstant objectType)
    {
        if (objectType is not { Kind: TypedConstantKind.Type } || returnType is null)
        {
            return false;
        }

        var genericType = objectType.Value as ITypeSymbol;

        if (genericType == null)
        {
            return false;
        }
        if (returnType.Value.ObjectType == null)
        {
            return false;
        }

        // Exact type match
        if (SymbolEqualityComparer.Default.Equals(returnType.Value.ObjectType, genericType))
        {
            return true;
        }

        // Check if returnType is derived from genericType (handles inheritance cases)
        var currentType = returnType.Value.ObjectType;
        while (currentType != null)
        {
            if (SymbolEqualityComparer.Default.Equals(currentType, genericType))
            {
                return true;
            }

            // Check interfaces
            foreach (var interfaceType in currentType.Interfaces)
            {
                if (SymbolEqualityComparer.Default.Equals(interfaceType, genericType))
                {
                    return true;
                }
            }

            currentType = currentType.BaseType;
        }

        return false;
    }


    public static bool IsMatchingStatusCode(ITypeSymbol? returnType, TypedConstant statusCode)
    {
        if (returnType == null ||statusCode.Value == null)
            return false;


        if (IsStatusCodeResultType(returnType) &&
            statusCode is not
            {
                Kind: TypedConstantKind.Enum
            })
        {
            var statusName = ((HttpStatusCode)statusCode.Value).ToString().ToLowerInvariant();

            return returnType.Name.ToLowerInvariant().Contains(statusName);
        }

        return false;
    }

   public static bool IsMatchingStatusCode(ITypeSymbol? returnType, int statusCode)
    {
        if (returnType == null)
            return false;

        // First check if this is a StatusCodeResult or inherits from it
        if (IsStatusCodeResultType(returnType))
        {
            if (TryGetKnownStatusCode(returnType.Name, out var knownStatusCode) ||
                TryGetStatusCode(returnType, out knownStatusCode))
            {
                return statusCode == knownStatusCode;
            }
        }

        return false;
    }

   private static bool TryGetStatusCode(ITypeSymbol type, out int statusCode)
    {
        // TODO: doesn't work yet

        statusCode = 0;
        return false;
    }

    private static bool IsStatusCodeResultType(ITypeSymbol type)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            if (current.Name == "StatusCodeResult" || current.Name == "ObjectResult")
                return true;
        }
        return false;
    }

    private static bool TryGetKnownStatusCode(string typeName, out int statusCode)
    {
        // Predefined mapping of common result types
        switch (typeName)
        {
            case "OkResult":
            case "OkObjectResult":
                statusCode = 200;
                return true;
            case "CreatedResult":
            case "CreatedAtActionResult":
            case "CreatedAtRouteResult":
                statusCode = 201;
                return true;
            case "AcceptedResult":
                statusCode = 202;
                return true;
            case "NoContentResult":
                statusCode = 204;
                return true;
            case "BadRequestResult":
            case "BadRequestObjectResult":
                statusCode = 400;
                return true;
            case "UnauthorizedResult":
                statusCode = 401;
                return true;
            case "ForbidResult":
                statusCode = 403;
                return true;
            case "NotFoundResult":
            case "NotFoundObjectResult":
                statusCode = 404;
                return true;
            case "ConflictResult":
                statusCode = 409;
                return true;
            default:
                // Try to infer from name for custom types
                if (typeName.Contains("Ok")) { statusCode = 200; return true; }
                if (typeName.Contains("Created")) { statusCode = 201; return true; }
                if (typeName.Contains("Accepted")) { statusCode = 202; return true; }
                if (typeName.Contains("NoContent")) { statusCode = 204; return true; }
                if (typeName.Contains("BadRequest")) { statusCode = 400; return true; }
                if (typeName.Contains("Unauthorized")) { statusCode = 401; return true; }
                if (typeName.Contains("Forbidden")) { statusCode = 403; return true; }
                if (typeName.Contains("NotFound")) { statusCode = 404; return true; }
                if (typeName.Contains("Conflict")) { statusCode = 409; return true; }

                statusCode = 0;
                return false;
        }
    }
    private class ReturnTypeEqualityComparer : IEqualityComparer<ReturnType>
    {
        public bool Equals(ReturnType x, ReturnType y) =>
            SymbolEqualityComparer.Default.Equals(x.Type, y.Type) &&
            ((x.ObjectType == null && y.ObjectType == null) ||
             (x.ObjectType != null && y.ObjectType != null &&
              SymbolEqualityComparer.Default.Equals(x.ObjectType, y.ObjectType)));

        public int GetHashCode(ReturnType obj)
        {
            int hash = SymbolEqualityComparer.Default.GetHashCode(obj.Type);
            if (obj.ObjectType != null)
                hash = hash * 31 + SymbolEqualityComparer.Default.GetHashCode(obj.ObjectType);
            return hash;
        }
    }
}

