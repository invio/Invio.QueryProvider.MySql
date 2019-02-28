namespace Invio.QueryProvider.MySql

open System
open System.Collections.Generic
open System.ComponentModel
open System.Linq
open System.Linq.Expressions
open System.Reflection

open Microsoft.FSharp.Linq.RuntimeHelpers
open Microsoft.FSharp.Quotations
open Microsoft.FSharp.Reflection
open MySql.Data.MySqlClient;

open Invio.Extensions.Reflection
open Invio.QueryProvider
open Invio.QueryProvider.ExpressionHelper
open Invio.QueryProvider.ExpressionMatching
open Invio.QueryProvider.TypeHelper
open Invio.QueryProvider.MySql.QueryTranslatorUtilities
open Invio.QueryProvider.MySql.DataReader

module QueryTranslator =
    let toLinq (expr : Expr<'a -> 'b>) =
      let linq = LeafExpressionConverter.QuotationToExpression expr
      let call = linq :?> MethodCallExpression
      let lambda = call.Arguments.[0] :?> LambdaExpression
      Expression.Lambda<Func<'a, 'b>>(lambda.Body, lambda.Parameters)

    let getDataType t =
        match System.Type.GetTypeCode(t) with
        | System.TypeCode.Boolean -> DataType MySqlDbType.Bit
        | System.TypeCode.Byte -> DataType MySqlDbType.Byte
        | System.TypeCode.Char -> DataType MySqlDbType.VarChar
        | System.TypeCode.DateTime -> DataType MySqlDbType.DateTime
        | System.TypeCode.Decimal -> DataType MySqlDbType.Decimal
        | System.TypeCode.Double -> DataType MySqlDbType.Double
        | System.TypeCode.Int16 -> DataType MySqlDbType.Int16
        | System.TypeCode.Int32 -> DataType MySqlDbType.Int32
        | System.TypeCode.Int64 -> DataType MySqlDbType.Int64
        | System.TypeCode.SByte -> DataType MySqlDbType.Byte
        | System.TypeCode.Single -> DataType MySqlDbType.Float
        | System.TypeCode.String -> DataType MySqlDbType.VarChar
        | System.TypeCode.UInt16 -> DataType MySqlDbType.Int16
        | System.TypeCode.UInt32 -> DataType MySqlDbType.Int32
        | System.TypeCode.UInt64 -> DataType MySqlDbType.Int64
        | System.TypeCode.Empty -> Unhandled
        | System.TypeCode.Object when typedefof<Guid>.IsAssignableFrom(t) -> DataType MySqlDbType.VarChar
        | System.TypeCode.Object when typedefof<Uri>.IsAssignableFrom(t) -> DataType MySqlDbType.VarChar
        | System.TypeCode.Object when typedefof<Char[]>.IsAssignableFrom(t) -> DataType MySqlDbType.VarChar
        | System.TypeCode.Object when typedefof<Byte[]>.IsAssignableFrom(t) -> DataType MySqlDbType.VarBinary
        | _ -> Unhandled

    let defaultGetMySqlDBType (typeSource : TypeSource) : (MySqlDbType DBType*Type) =
        let originType =
            match typeSource with
            | Method m -> m.ReturnType
            | Property p -> p.PropertyType
            | TypeSource.Value v -> v.GetType()
            | TypeSource.Type t -> t
        (originType |> unwrapType |> getDataType, originType)

    let isWellKnownType t =
        match getDataType t with
            | Unhandled -> false
            | _ -> true

    let private defaultGetTableName (t:System.Type) : string =
        t.Name
    let private defaultGetColumnName (t:System.Reflection.MemberInfo) : string =
        t.Name

    let private makeConverterLambda (destinationType : Type) (converter : TypeConverter) =
        toLinq (
            <@ fun (o : obj) ->
                match o with
                    | null -> null
                    | :? DBNull -> null
                    | v when destinationType.IsAssignableFrom(v.GetType()) -> v
                    | v when converter.CanConvertFrom(v.GetType()) -> converter.ConvertFrom(o)
                    | _ -> o @>)

    //terible duplication of code between this and createTypeSelect. needs to be refactored.
    let private createTypeConstructionInfo selectIndex (t : System.Type) : TypeConstructionInfo =
        if isValueType t then
            {
                Type = t
                ConstructorArgs = [Value selectIndex]
                PropertySets = []
            }
        else
            let rec createConstructorInfoForPropertyType index (propertyType : System.Type) =
                if propertyType = typedefof<bool> then
                    Bool index
                else if propertyType = typedefof<System.DateTime> then
                    DateTime index
                else if isEnumType propertyType then
                    Enum {
                        Type = propertyType
                        Index = index
                    }
                else if isNullable propertyType then
                    let underlyingType = Nullable.GetUnderlyingType(propertyType)
                    Type {
                        Type = propertyType
                        ConstructorArgs = [(createConstructorInfoForPropertyType index underlyingType)]
                        PropertySets = []
                    }
                else if isOption propertyType then
                    Type {
                        Type = propertyType
                        ConstructorArgs = [Value index]
                        PropertySets = []
                    }
                else if isValueType propertyType &&  isWellKnownType propertyType then
                    Value index
                else if propertyType.IsArray && propertyType.GetElementType() = typedefof<byte> then
                    Value index
                else if propertyType = typedefof<Uri> then
                    Type {
                        Type = propertyType
                        ConstructorArgs = [Value index]
                        PropertySets = []
                    }
                else
                    let typeConverter = TypeDescriptor.GetConverter propertyType
                    if typeConverter <> null then
                        let expr = (makeConverterLambda propertyType typeConverter) :> LambdaExpression
                        Lambda {
                            Lambda = expr
                            Delegate = expr.Compile()
                            Parameters = [Value index]
                        }
                    else
                        failwithf "The property type %s is not supported" propertyType.Name

            let createConstructorInfoFromProperties (fields : PropertyInfo list) =
                let ctorArgs = fields |> Seq.mapi(fun i f ->
                    let selectIndex = (selectIndex + i)
                    createConstructorInfoForPropertyType selectIndex f.PropertyType
                )

                {
                    Type = t
                    // Don't recomute the constructor args on every row
                    ConstructorArgs = ctorArgs |> Seq.toList
                    PropertySets = []
                }
            if FSharpType.IsRecord t then
                let fields = FSharpType.GetRecordFields t |> Seq.toList

                createConstructorInfoFromProperties fields
            else if t = typeof<Uri> then
                {
                    Type = t
                    ConstructorArgs = [Value 0]
                    PropertySets = []
                }
            else

                let bindingFlags =
                    BindingFlags.Public ||| BindingFlags.IgnoreCase
                    |||BindingFlags.Instance ||| BindingFlags.FlattenHierarchy

                let fields =
                    t.GetConstructors().Single().GetParameters()
                    |> Seq.toList
                    |> List.map(fun parameter -> t.GetProperty(parameter.Name, bindingFlags))

                createConstructorInfoFromProperties fields

    let private createConstructionInfoForType selectIndex (t : System.Type) returnType : ConstructionInfo =
        let typeCtor = createTypeConstructionInfo selectIndex t
        {
            ReturnType = returnType
            Type = typeCtor.Type
            TypeOrLambda = TypeOrLambdaConstructionInfo.Type typeCtor
            PostProcess = None
        }

    //terible duplication of code between this and createTypeSelect. needs to be refactored.
    let private createTypeSelect
        (getColumnName : System.Reflection.MemberInfo -> string)
        (tableAlias : string list)
        (topQuery : bool)
        (t : System.Type) =

        let createTypeSelectFromProperties fields =
            let query =
                fields
                |> List.map(fun  f -> tableAlias @ [".`"; getColumnName f; "`"])
                |> List.interpolate([[", "]])
                |> List.reduce(@)

            let query = query @ [" "]

            let ctor =
                if topQuery then
                    Some (createTypeConstructionInfo 0 t)
//                    let typeCtor = createTypeConstructionInfo 0 t
//                    Some {
//                        ReturnType = returnType
//                        Type = typeCtor.Type
//                        TypeOrLambda = TypeOrLambdaConstructionInfo.Type typeCtor
//                    }
                else
                    None

            query, ctor

        // need to call a function here so that this can be extended
        if FSharpType.IsRecord t then
            let fields = FSharpType.GetRecordFields t |> Seq.toList

            createTypeSelectFromProperties fields
        else
            let bindingFlags =
                BindingFlags.Public ||| BindingFlags.IgnoreCase
                |||BindingFlags.Instance ||| BindingFlags.FlattenHierarchy

            let fields =
                t.GetConstructors().Single().GetParameters()
                |> Seq.toList
                |> List.map(fun parameter -> t.GetProperty(parameter.Name, bindingFlags))

            createTypeSelectFromProperties fields

    let private createQueryableCtorInfo queryable returnType =
        createConstructionInfoForType 0 (TypeHelper.getElementType (queryable.GetType())) returnType

    let private groupByMethodInfo = lazy (
        typedefof<System.Linq.Enumerable>.GetTypeInfo().GetMethods()
        |> Seq.filter(fun mi -> mi.Name = "GroupBy")
        |> Seq.filter(fun mi ->
            let args = mi.GetParameters()
            if args.Length <> 2 then
                false
            else
                let first = args |> Seq.head
                let second = args |> Seq.last
                let firstEqual = (first.ParameterType.GetGenericTypeDefinition() = typedefof<System.Collections.Generic.IEnumerable<_>>)
                let secondEqual = (second.ParameterType.GetGenericTypeDefinition() = typedefof<System.Func<_, _>>)
                firstEqual && secondEqual
        )
        |> Seq.exactlyOne
    )

    let private nullableHasValue = (typedefof<Nullable<int>>).GetGenericTypeDefinition().GetProperty("HasValue");

    let private isNullableHasValue (m : MemberInfo) =
        m :? PropertyInfo &&
            m.DeclaringType.GetGenericTypeDefinition() = nullableHasValue.DeclaringType &&
            m.Name = nullableHasValue.Name

    /// <summary>
    /// Takes a Linq.Expression tree and produces a sql query and DataReader.ConstructionInfo to construct the resulting data.
    /// </summary>
    /// <param name="queryType"></param>
    /// <param name="getDBType">Called to determine the DbType for a TypeSource</param>
    /// <param name="getTableName">Called to determine the table name for a Type</param>
    /// <param name="getColumnName">Called to determine the column name for a Reflection.MemberInfo</param>
    /// <param name="expression">The Linq.Expression to translate</param>
    let translateToStatement
        (getStorageType : (TypeSource -> Type) option)
        (getTableName : GetTableName option)
        (getColumnName : GetColumnName option)
        (expression : Expression) =

        let getMappedType (t : Type) =
            match getStorageType with
                | Some fn ->
                    match fn (TypeSource.Type t) with
                        | null -> t
                        | mappedType -> mappedType
                | None -> t

        let getDBType ( typeSource: TypeSource) =
            let typeSource =
                match getStorageType with
                    | Some fn ->
                        match fn typeSource with
                            | null -> typeSource
                            | t -> TypeSource.Type t
                    | None -> typeSource
            match defaultGetMySqlDBType typeSource with
            | (Unhandled, _) -> failwithf "Could not determine DataType for '%A' is not handled" typeSource
            | (DataType dbType, t) -> (dbType, t)

        let getColumnName =
            match getColumnName with
            | Some g -> fun t ->
                match g t with
                | Some r ->  r
                | None -> defaultGetColumnName t
            | None -> defaultGetColumnName
        let getTableName =
            match getTableName with
            | Some g -> fun t ->
                match g t with
                | Some r ->  r
                | None -> defaultGetTableName t
            | None -> defaultGetTableName

        let columnNameUnique = ref 0

        let getNextParamIndex () =
            columnNameUnique := (!columnNameUnique + 1)
            !columnNameUnique


        let createParameter value =
            let (dbType, dotnetType) = getDBType (TypeSource.Value value)
            createParameter (getNextParamIndex()) value dbType dotnetType

        let tableAliasIndex = ref 1

        let getTableAlias () =
            let a =
                match !tableAliasIndex with
                | 1 -> ["T"]
                | i -> ["T"; i.ToString()]
            tableAliasIndex := (!tableAliasIndex + 1)
            a

        let createTypeSelect t context =
            let tableAlias =
                match context.TableAlias with
                    | Some alias -> alias
                    | None -> getTableAlias()
            let q, ctor = createTypeSelect getColumnName tableAlias context.TopQuery t
            q, [], ctor
        let createFullTypeSelect t returnType context =
            let q, p, ctor = createTypeSelect t context
            let ctor =
                match ctor with
                | None -> None
                | Some ctor ->
                    Some {
                        ReturnType = returnType
                        Type = ctor.Type
                        TypeOrLambda = TypeOrLambdaConstructionInfo.Type ctor
                        PostProcess = None
                    }
            q, p, ctor

        let generateManualSqlQuery (queryable : IQueryable) (tableAlias : string list) context =
            match queryable with
            | :? QueryOperations.ISqlQuery as sql ->
                let namedParams =
                    sql.Parameters |> Seq.map(fun p ->
                        p.Name, lazy (createParameter p.Value)
                    ) |> Map.ofSeq

                let query, interoplatedParams =
                    sql.Query |>
                    Seq.fold(fun (acumQ, acumP) query ->
                        let q, p =
                            match query with
                            | QueryOperations.S text -> text, []
                            | QueryOperations.P value ->
                                let p = createParameter value
                                p.Name, [p]
                            | QueryOperations.NP name ->
                                let p =
                                    match namedParams |> Map.tryFind name with
                                    | Some p -> p.Value
                                    | None -> failwithf "Invalid query, no such param '%s'" name

                                p.Name, []
                        acumQ @ [q], acumP @ p
                    ) ([], [])

                Some (query, interoplatedParams @ (namedParams |> Map.toList |> List.map(fun (_, p) -> p.Value)))
            | _ ->  None

        let rec mapFun (context : Context) e : ExpressionResult * (string list * PreparedParameter<_> list * ConstructionInfo list) =
            let mapd = fun context e ->
                mapd(mapFun) context e |> splitResults
            let map = fun e ->
                mapd context e

            let valueToQueryAndParam (dbType : MySqlDbType) (dotnetType : Type) value =
                valueToQueryAndParam (getNextParamIndex()) dbType dotnetType value

            let bin (e : BinaryExpression) (operator : string) =
                let leftSql, leftParams, leftCtor = map(e.Left)
                let rightSql, rightParams, rightCtor = map(e.Right)
                let parameters = leftParams @ rightParams
                let ctors = leftCtor @ rightCtor
                let sql =
                    if (getMappedType e.Left.Type) = typedefof<string> then
                        leftSql @ [" COLLATE utf8_bin "; operator; " "] @ rightSql
                    else
                        leftSql @ [" "; operator; " "] @ rightSql
                (["("] @ sql @ [")"], parameters, ctors)

            let isNullExpr (e : Expression) =
                match (getLocalValue e) with
                    | Some v when v = null -> true
                    | _ -> false

            let result : option<string list * PreparedParameter<_> list * ConstructionInfo list>=
                match e with
                | Call m ->
                    let linqChain =
                        getOperationsAndQueryable m

                    match linqChain with
                    | Some (queryable, ml) ->
                        let originalMl = ml

                        let select, ml = getMethod "Select" ml
                        let wheres, ml = getMethods ["Where"] ml
                        let count, ml = getMethod "Count" ml
                        let last, ml = getMethod "Last" ml
                        let lastOrDefault, ml= getMethod "LastOrDefault" ml
                        let contains, ml = getMethod "Contains" ml
                        let single, ml = getMethod "Single" ml
                        let singleOrDefault, ml = getMethod "SingleOrDefault" ml
                        let first, ml = getMethod "First" ml
                        let firstOrDefault, ml = getMethod "FirstOrDefault" ml
                        let skip, ml = getMethod "Skip" ml
                        let take, ml = getMethod "Take" ml
                        let max, ml = getMethod "Max" ml
                        let min, ml = getMethod "Min" ml
                        let sum, ml = getMethod "Sum" ml
                        let any, ml = getMethod "Any" ml
                        let groupBy, ml = getMethod "GroupBy" ml
                        let nothing, ml = getMethod "nothing" ml

                        let singleMethodOnlyAllowed = [
                            "Select";
                            "Count";
                            "Last";
                            "LastOrDefault";
                            "Contains";
                            "Single";
                            "SingleOrDefault";
                            "First";
                            "FirstOrDefault";
                            "Skip";
                            "Take";
                            "Max";
                            "Min";
                            "Sum";
                            "Any";
                            "GroupBy";
                            "nothing"
                        ]

                        let wheresLists = [any; single; singleOrDefault; first; firstOrDefault]
                        let concatWheres original extend =
                            match extend with
                            | None -> original
                            | HasWhereClause extend ->
                                original @ [extend]
                            | _ -> original

                        let wheres = List.fold concatWheres wheres wheresLists

                        let sorts, ml= getMethods ["OrderBy"; "OrderByDescending"; "ThenBy"; "ThenByDescending"] ml
                        let sorts, maxOrMin =
                            let m =
                                match max,min with
                                | Some _, Some _ -> failwith "invalid"
                                | Some m, None -> Some(m)
                                | None, Some m -> Some(m)
                                | None, None -> None
                            match m with
                            | Some _ -> sorts @ [m.Value], m
                            | None -> sorts, m

                        let needsSelect = lazy ([count; contains; any; maxOrMin; sum; select] |> Seq.exists(Option.isSome))
                        let hasTooMany, ml = getMethods singleMethodOnlyAllowed ml

                        if hasTooMany |> Seq.length > 0 then
                            let methodNames = (hasTooMany |> Seq.map(fun m -> sprintf "'%s'" m.Method.Name) |> String.concat(","))
                            failwithf "Query can only contain one call to the following: %s" methodNames

                        if ml |> Seq.length > 0 then
                            let methodNames = (ml |> Seq.map(fun m -> sprintf "'%s'" m.Method.Name) |> String.concat(","))
                            failwithf "Methods not implemented: %s" methodNames

                        if last.IsSome then
                            failwith "'last' operator has no translations for MySql"
                        if lastOrDefault.IsSome then
                            failwith "'lastOrDefault' operator has no translations for MySql"

                        let tableAlias = (getTableAlias())
                        let aliasedContext =
                            {context with TableAlias = Some tableAlias}

                        let map e =
                            mapd {aliasedContext with TopQuery = false} e

                        let manualSqlQuery, (manualSqlParams : PreparedParameter<MySqlDbType> list), manualSqlOverride =
                            match generateManualSqlQuery queryable tableAlias aliasedContext with
                            | Some (q, p) -> q, p, true
                            | None -> [], [], false

                        let getReturnType () =
                            let isSome (o : option<_>) = o.IsSome
                            if isSome single || isSome first || isSome max || isSome min then
                                Single
                            else if isSome singleOrDefault || isSome firstOrDefault then
                                SingleOrDefault
                            else
                                Many

                        let selectClause, selectParameters, selectCtor =
                            let selectColumns, selectParameters, selectCtor =
                                match count with
                                | Some _->
                                    ["COUNT(*) "], [], (Some (createConstructionInfoForType 0 typedefof<int> Single))
                                | None ->
                                    if contains.IsSome || any.IsSome then
                                        ["COUNT(1) > 0 "], [] , (Some (createConstructionInfoForType 0 typedefof<bool> Single))
                                    else if sum.IsSome then
                                        let sum = sum.Value
                                        let l = getLambda(sum)
                                        let columns, _, _ =
                                            match l.Body with
                                            | MemberAccess m -> map m
                                            | _ -> failwith "not implemented lambda body"
                                        ["SUM("] @ columns @ [") "], [], (Some (createConstructionInfoForType 0 l.ReturnType Single))
                                    else
                                        let partialSelect (l : LambdaExpression) =
                                            let t = l.ReturnType
                                            let c =
                                                if context.TopQuery then
                                                    Some (createConstructionInfoForType 0 t (getReturnType()))
                                                else
                                                    None
                                            let q, p, _ = (l.Body |> map)
                                            q @ [" "], p, c
                                        match maxOrMin with
                                        | Some m ->
                                            partialSelect (getLambda m)
                                        | None ->
                                            match select with
                                            | Some select ->
                                                match getLambda(select) with
                                                | SingleSameSelect x -> createFullTypeSelect x.Type (getReturnType()) aliasedContext
                                                | l ->
                                                    match l.Body with
                                                    | MemberAccess _ -> partialSelect l
                                                    | Call _m  ->
                                                        if not context.TopQuery then
                                                            failwith "Calls are only allowed in top select"
                                                        let rec isParameter e =
                                                            match e with
                                                            | Parameter _ -> true
                                                            | MemberAccess m -> isParameter m.Expression
                                                            | _ -> false

                                                        //take arguments, map to new argument sequence
                                                        // check if the node type is parameter, transform it then
                                                        // select all arguments where node type is parameter
                                                        //failwith "not implemented call"
                                                        let selectQuery, selectParams, typeCtor =
                                                            createTypeSelect (TypeHelper.getElementType (queryable.GetType())) aliasedContext
                                                        let typeCtor =
                                                            match typeCtor with
                                                            | Some typeCtor -> typeCtor
                                                            | None -> failwith "shouldnt be none"

                                                        let lambdaCtor = {
                                                            Lambda = l
                                                            Delegate = l.Compile()
                                                            Parameters = [Type typeCtor]
                                                        }

                                                        let ctor = {
                                                            Type = l.ReturnType
                                                            ReturnType = getReturnType()
                                                            TypeOrLambda = TypeOrLambdaConstructionInfo.Lambda lambdaCtor
                                                            PostProcess = None
                                                        }

                                                        selectQuery, selectParams, Some ctor
                                                    | _ -> failwith "not implemented lambda body"
                                            | None ->
                                                if not manualSqlOverride then
                                                    createFullTypeSelect (TypeHelper.getElementType (queryable.GetType())) (getReturnType ()) aliasedContext
                                                else
                                                    if context.TopQuery then
                                                        [] ,[], Some(createQueryableCtorInfo queryable (getReturnType()))
                                                    else
                                                        [], [], None

                            let selectCtor =
                                match selectCtor with
                                | Some selectCtor ->
                                    let constructed =
                                        match groupBy with
                                        | Some groupBy ->
                                            if not context.TopQuery then
                                                failwith "Grouping only supported on top query" //would be possible to support, not doing for now though
                                            let selector = getLambda groupBy

                                            let sourceType = typedefof<System.Collections.Generic.IEnumerable<_>>.MakeGenericType(selectCtor.Type)
                                            let oldArgs = groupBy.Method.GetGenericArguments()
                                            let constructedGroupBy = groupByMethodInfo.Value.MakeGenericMethod(oldArgs |> Seq.head, oldArgs |> Seq.last)
                                            let source = Expression.Parameter(sourceType)
                                            let body = Expression.Call(constructedGroupBy, source, selector)
                                            let lambda = Expression.Lambda(body, [source])

                                            { selectCtor with
                                                PostProcess = Some lambda }
                                        | None -> selectCtor
                                    [constructed]
                                | None -> []

                            let frontSelect =
                                if not manualSqlOverride || needsSelect.Value then
                                    ["SELECT "]
                                else
                                    []

                            frontSelect @ selectColumns, selectParameters, selectCtor

                        let from =
                            if not manualSqlOverride || needsSelect.Value then
                                ["FROM `"; getTableName(queryable.ElementType); "` AS "; ] @ tableAlias
                            else
                                []

                        let mainStatement = selectClause @ from

                        let whereClause, whereParameters, whereCtor =
                            let fromWhere =
                                match wheres with
                                | [] -> None
                                | _ ->
                                    let wheres, parameters, ctors =
                                        wheres |> List.rev |> List.map(fun w ->
                                            let b = getLambda(w).Body
                                            let x =
                                                match b with
                                                | Call m when(m.Method.Name = "Contains") ->
                                                    //(PersonId IN (SELECT PersonID FROM Employee))
                                                    match m with
                                                    | CallIQueryable(_m, q, rest) ->
                                                        let containsVal = rest |> Seq.head
                                                        match containsVal with
                                                        | MemberAccess a ->
                                                            let accessQ, accessP, accessCtor = a |> map
                                                            let subQ, subP, subCtor = q |> map
                                                            Some ([accessQ @ [" IN ("] @ subQ @ [")"]], accessP @ subP, accessCtor @ subCtor)
                                                        | _ ->
                                                            None
                                                    | _ -> None
                                                | _ -> None
                                            match x with
                                            | None ->
                                                let q, p, c = b |> map
                                                [q], p, c
                                            | Some x ->
                                                x
                                        ) |> splitResults
                                    let sql = wheres |> List.interpolate [[" AND "]] |> List.reduce(@)
                                    Some (sql, parameters, ctors)

                            let fromContains =
                                match contains with
                                | Some c ->
                                    let xq, xp, xc = getLambda(select.Value).Body |> map
                                    let yq, yp, yc = c.Arguments.Item(1) |> map
                                    Some (["("] @ xq @ [" = "] @ yq @ [")"], xp @ yp, xc @ yc)
                                | None -> None

                            let total =
                                match fromWhere, fromContains with
                                | None, None -> None
                                | Some w, None -> Some w
                                | None, Some c -> Some c
                                | Some(wq,wp,wc), Some(cq,cp,cc) -> Some(["("] @ wq @ [" AND "] @ cq @ [")"], wp @ cp, wc @ cc)

                            match total with
                            | None -> [], [], []
                            | Some (q, qp, qc) -> [" WHERE ("] @ q @ [")"], qp, qc

                        let orderByClause, orderByParameters, orderByCtor =
                            match sorts with
                            | [] -> [], [], []
                            | _ ->
                                let colSorts, parameters, ctor =
                                    sorts |> List.rev |> List.map(fun s ->
                                        let sortMethod =
                                            match s.Method.Name with
                                            | "Min" | "OrderBy" | "ThenBy" -> "ASC"
                                            | "Max" | "OrderByDescending" | "ThenByDescending" -> "DESC"
                                            | n -> failwithf "Sort methods not implemented '%s'" n
                                        let lambda = getLambda(s)
                                        let sql, parameters, ctor = (lambda.Body |> map)
                                        [sql @ [" "; sortMethod]], parameters, ctor
                                    ) |> splitResults

                                //let colSorts = colSorts |> List.interpolate [", "]
                                let colSorts = colSorts |> List.interpolate [[", "]] |> List.reduce(@)

                                [" ORDER BY "] @ colSorts, parameters, ctor

                        let limitStatement =
                            let skipCount, count =
                                if single.IsSome || singleOrDefault.IsSome then
                                    None, Some 2
                                else if
                                    first.IsSome ||
                                    firstOrDefault.IsSome ||
                                    max.IsSome ||
                                    min.IsSome then
                                    None, Some 1
                                else if take.IsSome && skip.IsSome then
                                    let takeSkipComparison = compareMethodIndexes "Skip" "Take" originalMl
                                    let skipValue = getInt skip.Value
                                    let takeValue = getInt take.Value
                                    match takeSkipComparison with
                                    | i  when i > 0 -> Some skipValue, Some takeValue
                                    // When take comes before skip you'll skip within the existing set. So subtract
                                    // the skip count from the take count and obviously no reason to have a
                                    // take statement with negative numbers.
                                    | i  when i < 0 -> Some skipValue, Some (Math.Max(takeValue - skipValue, 0))
                                    | _ -> failwithf "Couldn't determine if skip came before take."

                                else if take.IsSome then
                                    None, Some (getInt take.Value)
                                else if skip.IsSome then
                                    failwithf "Query must contain take if skip is specified."
                                else
                                    None, None

                            if skipCount.IsSome && count.IsSome then
                                [" LIMIT "; count.Value.ToString(); " OFFSET "; skipCount.Value.ToString()]
                            else if count.IsSome then
                                [" LIMIT "; count.Value.ToString()]
                            else
                                []

                        let sql =  mainStatement @ manualSqlQuery @ whereClause @ orderByClause @ limitStatement
                        let parameters = manualSqlParams @ orderByParameters @ whereParameters @ selectParameters
                        let ctor = orderByCtor @ whereCtor @ selectCtor
                        Some (sql, parameters, ctor)
                    | None ->
                        let simpleInvoke m =
                            let v = invoke m
                            let (dbType, dotnetType) = getDBType (TypeSource.Value v)
                            Some (v |> valueToQueryAndParam dbType dotnetType)

                        match m.Method.Name with
                        | "Contains" | "StartsWith" | "EndsWith" as typeName
                            when(m.Object <> null && m.Object.Type = typedefof<string>) ->
                            let value = getRequiredLocalValue (m.Arguments.Item(0))
                            let (dbType, dotnetType) = getDBType (TypeSource.Value value)
                            let valQ, valP, valC = valueToQueryAndParam dbType dotnetType value
                            let search =
                                match typeName with
                                | "Contains" -> ["CONCAT('%', "] @ valQ @ [", '%')"]
                                | "StartsWith" -> ["CONCAT("] @ valQ @ [", '%')"]
                                | "EndsWith" -> ["CONCAT('%', "] @ valQ @ [")"]
                                | _ -> failwithf "not implemented %s" typeName
                            let colQ, colP, colC = map(m.Object)

                            let comp =
                                if (m.Arguments.Count = 2) then
                                    (getRequiredLocalValue (m.Arguments.Item(1))) :?> StringComparison
                                else
                                    StringComparison.Ordinal
                            let collation =
                                match comp with
                                    | StringComparison.InvariantCulture
                                    | StringComparison.Ordinal -> "utf8_bin"
                                    // This isn't entirely accurate. The utf8_unicode_ci collation ignores diacritics
                                    // whereas c# does not. Unfortunately there is no MySql collation that behaves
                                    // precisely like the ...IgnoreCase comparisons.
                                    | StringComparison.InvariantCultureIgnoreCase
                                    | StringComparison.OrdinalIgnoreCase -> "utf8_unicode_ci"
                                    | _ -> failwithf "StringComparison %s not implemented" (comp.ToString())
                            Some (colQ @ [" COLLATE "; collation; " LIKE "] @ search, colP @ valP, colC @ valC)
                        | "Equals" when (m.Method.DeclaringType = typedefof<string>) ->
                            let (expr, value, compIx) =
                                if m.Object <> null then
                                    // Instance method, assume obj.Prop.Equals(String[, StringComparison])
                                    (m.Object, getRequiredLocalValue (m.Arguments.Item(0)), 1)
                                else
                                    // Static method, assume String.Equals(obj.Prop, String[, StringComparison])
                                    (m.Arguments.Item(0), getRequiredLocalValue (m.Arguments.Item(1)), 2)
                            let colQ, colP, colC = map(expr)
                            let (dbType, dotnetType) = getDBType (TypeSource.Value value)
                            let valQ, valP, valC = valueToQueryAndParam dbType dotnetType value
                            let comp =
                                if (m.Arguments.Count > compIx) then
                                    getRequiredLocalValue (m.Arguments.Item(compIx)) :?> StringComparison
                                else
                                    StringComparison.Ordinal
                            let collation =
                                match comp with
                                    // I believe these differ only in sort order
                                    | StringComparison.InvariantCulture
                                    | StringComparison.Ordinal -> "utf8_bin"
                                    // This isn't entirely accurate. The utf8_unicode_ci collation ignores diacritics
                                    // whereas c# does not. Unfortunately there is no MySql collation that behaves
                                    // precisely like the ...IgnoreCase comparisons. It's tempting to use the UPPER
                                    // function w/ the utf8_bin collation to ignore case, but there are some unicode
                                    // characters that are stripped out by the UPPER function, so that won't work.
                                    | StringComparison.InvariantCultureIgnoreCase
                                    | StringComparison.OrdinalIgnoreCase -> "utf8_unicode_ci"
                                    // CurrentCulture[IgnoreCase] is not supported since there is no easy way to
                                    // translate this into a MySql collation.
                                    | _ -> failwithf "StringComparison %s not implemented" (comp.ToString())
                            Some (colQ @ [" COLLATE "; collation; " = "] @ valQ, colP @ valP, colC @ valC)
                        | "Invoke" | "op_Dereference" ->
                            simpleInvoke m
                        | "Some" when (isOption m.Method.ReturnType) ->
                            simpleInvoke m
                        | "get_None" when (isOption m.Method.ReturnType) ->
                            let t = m.Method.ReturnType.GetGenericArguments() |> Seq.head
                            let (dbType, _) = (getDBType (TypeSource.Type t))
                            Some (["NULL"], [], [])
                        | "Contains" ->
                            match m with
                            | CallIEnumerable(_m, enumerableObject, args) ->
                                let firstArg = args |> Seq.head
                                let (dbType, dotnetType) = getDBType (TypeSource.Type firstArg.Type)
                                let queryParams =
                                    enumerableObject
                                    |> Seq.cast<Object>
                                    |> Seq.map(fun v -> valueToQueryAndParam dbType dotnetType v)
                                    |> Seq.toList

                                let colQ, colP, colC = map(firstArg)
                                let colQCollated =
                                    if firstArg.Type = typedefof<string> then
                                        if args.Length = 2 then
                                            let cmp = getRequiredLocalValue args.[1] :?> IEqualityComparer<String>
                                            if cmp = (StringComparer.InvariantCulture :> IEqualityComparer<String>) ||
                                                cmp = (StringComparer.Ordinal :> IEqualityComparer<String>) then
                                                colQ @ [ " COLLATE utf8_bin " ]
                                            else if cmp = (StringComparer.InvariantCultureIgnoreCase :> IEqualityComparer<String>) ||
                                                cmp = (StringComparer.OrdinalIgnoreCase :> IEqualityComparer<String>) then
                                                colQ @ [ " COLLATE utf8_unicode_ci " ]
                                            else failwith "The specified StringComparer is not supported"
                                        else colQ @ [ " COLLATE utf8_bin " ]
                                    else
                                        colQ
                                let count = queryParams.Count()

                                if count = 0 then
                                    Some (["FALSE"], colP, colC)
                                else
                                    let inStatement =
                                        queryParams
                                        |> List.map (fun (a, _, _) -> a)
                                        |> List.interpolate [[", "]]
                                        |> List.reduce(@)

                                    let valPs =
                                        queryParams
                                        |> List.map (fun (_, b, _) -> b)
                                        |> List.reduce(@)

                                    let valCs =
                                        queryParams
                                        |> List.map (fun (_, _, c) -> c)
                                        |> List.reduce(@)

                                    Some (colQCollated @ [" IN ("] @ inStatement @ [")"], colP @ valPs, colC @ valCs)
                            | _ -> failwithf "Contains Method not supported for type."
                        | x ->
                            failwithf "Method '%s' is not implemented." x
                | Not n ->
                    let sql, parameters, ctor = map(n.Operand)
                    Some ([" NOT "] @ sql, parameters, ctor)
                | And e ->
                    let sqlOperator =
                        match isBinaryOperation e with
                            | true -> "&"
                            | false -> "AND"
                    Some (bin e sqlOperator)
                | AndAlso e -> Some (bin e "AND")
                | Or e ->
                    let sqlOperator =
                        match isBinaryOperation e with
                            | true -> "|"
                            | false -> "OR"
                    Some (bin e sqlOperator)
                | OrElse e -> Some (bin e "OR")
                | ExclusiveOr e -> Some (bin e "^")
                | Equal e when (isNullExpr e.Left || isNullExpr e.Right) -> Some (bin e "IS")
                | Equal e -> Some (bin e "<=>")
                | NotEqual e when (isNullExpr e.Left || isNullExpr e.Right) -> Some (bin e "IS NOT")
                | NotEqual e ->
                    let (sql, parameters, ctor) = bin e "<=>"
                    Some ([" NOT "] @ sql, parameters, ctor)
                | LessThan e -> Some (bin e "<")
                | LessThanOrEqual e -> Some (bin e "<=")
                | GreaterThan e -> Some (bin e ">")
                | GreaterThanOrEqual e -> Some (bin e ">=")
                | Constant c ->
                    let queryable =
                        match c.Value with
                        | :? IQueryable as v -> Some v
                        | _ -> None

                    match queryable with
                    | Some queryable when TypeExtensions.IsDerivativeOf(queryable.GetType(), typedefof<Query<_>>) ->
                        let tableAlias = getTableAlias()
                        let aliasedContext =
                            {context with TableAlias = Some tableAlias};
                        let selectColumns, selectParameters, selectCtor =
                            createFullTypeSelect (TypeHelper.getElementType (queryable.GetType())) Many aliasedContext
                        let from = ["FROM `"; getTableName(queryable.ElementType); "` AS "; ] @ tableAlias
                        Some (["SELECT "] @ selectColumns @ from, selectParameters, [createQueryableCtorInfo queryable Many])
                    | Some queryable ->
                        let tableAlias = getTableAlias()
                        let aliasedContext =
                            {context with TableAlias = Some tableAlias};
                        match generateManualSqlQuery queryable tableAlias aliasedContext with
                        | Some (q, p) ->
                            Some (q, p, [createQueryableCtorInfo queryable Many])
                        | None -> failwith "This should never get hit"
                    | None ->
                        if c.Value = null then
                            let (dbType, dotnetType) = (getDBType (TypeSource.Type c.Type))
                            Some (["NULL"], [], [])
                        else
                            let (dbType, dotnetType) = (getDBType (TypeSource.Value c.Value))
                            Some (valueToQueryAndParam dbType dotnetType c.Value)
                | MemberAccess m ->
                    if m.Expression <> null && m.Expression.NodeType = ExpressionType.Parameter then
                        // If the expression is a MemberAccess on a Parameter (i.e. a POCO property) generate column
                        // access SQL
                        match context.TableAlias with
                        | Some tableAlias -> Some (tableAlias @ [".`"; getColumnName(m.Member); "`"], [], [])
                        | None -> failwith "cannot access member without tablealias being genned"
                    else
                        match getLocalValue m with
                        // If the expression is a chain of MemberAccess and Call expressions on a constant, evaluate
                        // that in C#
                        | Some (value) when value = null -> Some (["NULL"], [], [])
                        | Some (value) ->
                            let (dbType, dotnetType) = (getDBType (TypeSource.Value value))
                            let param = valueToQueryAndParam dbType dotnetType value
                            Some (param)
                        | None ->
                            if isNullableHasValue m.Member then
                                let exprSql, exprParams, exprCtor = map(m.Expression)
                                let sql = exprSql @ [" IS NOT NULL"]
                                Some (["("] @ sql @ [")"], exprParams, exprCtor)
                            else
                                None
                | _ -> None

            match result with
            | Some r -> ExpressionResult.Skip, r
            | None -> ExpressionResult.Recurse, ([], [], [])

        let results =
            expression
            |> mapd(mapFun) ({TableAlias = None; TopQuery = true})

        let queryList, queryParameters, resultConstructionInfo = results |> splitResults

        let query = queryList |> String.concat("")

        {
            PreparedStatement.Text = query
            FormattedText = query
            Parameters = queryParameters
            ResultConstructionInfo =
                if resultConstructionInfo |> Seq.isEmpty then
                    None
                else
                    Some (resultConstructionInfo |> Seq.exactlyOne)
        }

    /// <summary>
    /// Create IDbCommand from a PreparedStatement
    /// </summary>
    /// <param name="connection"></param>
    /// <param name="preparedStatement"></param>
    let createCommand (connection : MySql.Data.MySqlClient.MySqlConnection) (preparedStatement : PreparedStatement<MySqlDbType>) =

        let cmd = connection.CreateCommand()
        cmd.CommandText <- preparedStatement.Text
        for param in preparedStatement.Parameters do
            let sqlParam = cmd.CreateParameter()
            sqlParam.ParameterName <- param.Name
            sqlParam.Value <- param.Value
            sqlParam.MySqlDbType <- param.DbType
            cmd.Parameters.Add(sqlParam) |> ignore
        cmd

    /// <summary>
    /// Translate a Linq.Expression to an IDbCommand
    /// </summary>
    /// <param name="queryType"></param>
    /// <param name="getDBType"></param>
    /// <param name="getTableName"></param>
    /// <param name="getColumnName"></param>
    /// <param name="connection"></param>
    /// <param name="expression"></param>
    let translateToCommand getDBType getTableName getColumnName connection expression =
        let ps = translateToStatement getDBType getTableName getColumnName expression

        let cmd = createCommand connection ps

        cmd, ps.ResultConstructionInfo
