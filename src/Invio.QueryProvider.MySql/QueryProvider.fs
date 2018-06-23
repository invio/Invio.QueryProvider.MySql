namespace Invio.QueryProvider.MySql

open System
open System.Collections
open System.Collections.Generic
open System.Data
open System.Linq.Expressions
open System.Linq
open System.Reflection
open System.Threading.Tasks

open MySql.Data.MySqlClient

open Invio.Extensions.Linq.Async
open Invio.Extensions.Reflection
open Invio.QueryProvider
open Invio.QueryProvider.MySql.QueryTranslator

type gfdi = delegate of Expression -> IEnumerable<obj>
/// <summary>
///   An <see creft="System.Linq.IQueryProvider" /> implementation for
///   <see cref="MySqlConnection" />.
/// </summary>
/// <param name="connection">
///   An open <see cref="MySqlConnection" /> instance.
/// </param>
type public MySqlQueryProvider ( connection : MySqlConnection ) =
    inherit BaseQueryProvider()

    let createCommandFromExpression con expression =
        let command, ctor =
            translateToCommand
                None
                None
                None
                con
                expression

        let ctor =
            match ctor with
            | Some ctor -> ctor
            | None -> failwith "no ctorinfor generated"
        command, ctor

    static let iQueryableType = typedefof<IQueryable<obj>>.GetGenericTypeDefinition()
    let isIQueryable (typedef : Type) = typedef.IsGenericType && typedef.GetGenericTypeDefinition() = iQueryableType
    let getQueryableInterface (typedef: Type) =
        if (isIQueryable typedef) then
            Some typedef
        else
            match (typedef.GetInterfaces()) |> Seq.where isIQueryable |> Seq.toList with
                | [ result ] -> Some result
                | _ -> None

    let unwrapQueryableType (typedef : Type) =
        match (getQueryableInterface typedef) with
            | Some queryableType -> queryableType.GetGenericArguments() |> Seq.exactlyOne
            | None -> failwithf "The specified type %s is not an IQueryable<T>" (typedef.GetNameWithGenericParameters())

    static let prepareEnumerableMethod =
        typedefof<MySqlQueryProvider>.GetMethods(BindingFlags.NonPublic ||| BindingFlags.Instance)
            |> Seq.where (fun m -> m.Name = "PrepareEnumerable" && m.IsGenericMethod && m.GetGenericArguments().Length = 1)
            |> Seq.exactlyOne

    override this.Execute expression =
        let cmd, ctorInfo = createCommandFromExpression connection expression
        use reader = cmd.ExecuteReader()
        DataReader.read reader ctorInfo

    override this.PrepareEnumerable expression =
        let prepareEnumerable = prepareEnumerableMethod.MakeGenericMethod(unwrapQueryableType <| expression.Type)
        let args : obj array = [| expression |]
        prepareEnumerable.Invoke(this, args) :?> IEnumerable

    member internal this.PrepareEnumerable<'T> (expression : Expression) : IEnumerable<'T> =
        let statement = translateToStatement None None None expression

        SqlCommandEnumerable<'T>(connection, statement) :> IEnumerable<'T>

    member this.ExecuteAsync (expression : Expression) : Async<obj> =
        async {
            let cmd, ctorInfo = createCommandFromExpression connection expression
            let asyncResult = cmd.BeginExecuteReader()
            let! _ = Async.AwaitIAsyncResult asyncResult
            use reader = cmd.EndExecuteReader asyncResult
            return DataReader.read reader ctorInfo
        }

    member this.ExecuteGenericAsync<'T> (expression : Expression) : Async<'T> =
        async {
            let! result = this.ExecuteAsync expression
            return match result with
                    | null -> Unchecked.defaultof<'T>
                    | x -> x :?> 'T
        }

    member this.GetEnumeratorAsync<'T> (expression : Expression) : Async<IAsyncEnumerator<'T>> =
        async {
            let statement = translateToStatement None None None expression
            let cmd = createCommandFromStatement connection statement
            let asyncResult = cmd.BeginExecuteReader()
            let! _ = Async.AwaitIAsyncResult asyncResult
            let reader = cmd.EndExecuteReader asyncResult

            return new SqlAsyncReaderEnumerator<'T>(statement, cmd, reader) :> IAsyncEnumerator<'T>
        }

    interface IAsyncQueryProvider with
        member this.ExecuteAsync expression = this.ExecuteAsync expression |> Async.StartAsTask

        member this.ExecuteAsync<'T> expression = this.ExecuteGenericAsync<'T> expression |> Async.StartAsTask

        member this.GetEnumeratorAsync<'T> expression = this.GetEnumeratorAsync<'T> expression |> Async.StartAsTask


and internal SqlCommandEnumerable<'T>
    (
        connection : MySqlConnection,
        statement : PreparedStatement<MySqlDbType>
    ) =

    interface IEnumerable<'T> with
        member this.GetEnumerator() =
            new SqlCommandEnumerator<'T>(connection, statement) :> IEnumerator<'T>

    interface IEnumerable with
        member this.GetEnumerator() =
            new SqlCommandEnumerator<'T>(connection, statement) :> IEnumerator

and internal SqlCommandEnumerator<'T>
    (
        connection : MySqlConnection,
        statement : PreparedStatement<MySqlDbType>
    ) =

    let command = lazy createCommandFromStatement connection statement
    let reader = lazy command.Force().ExecuteReader()
    let ctor =
        match statement.ResultConstructionInfo with
        | Some ctor -> ctor
        | None -> failwith "no ctorinfor generated"
    let innerEnumerable = lazy (DataReader.read (reader.Force()) ctor :?> IEnumerable<'T>)
    let innerEnumerator = lazy innerEnumerable.Force().GetEnumerator()

    interface IEnumerator<'T> with
        member this.Current = innerEnumerator.Force().Current

    interface IEnumerator with
        member this.MoveNext() = innerEnumerator.Force().MoveNext()

        member this.Current = innerEnumerator.Force().Current :> obj

        member this.Reset() = innerEnumerator.Force().Reset()

    interface IDisposable with
        member this.Dispose() =
            if reader.IsValueCreated then
                reader.Value.Dispose()
            if command.IsValueCreated then
                command.Value.Dispose()

and internal SqlAsyncReaderEnumerator<'T>
    (
        statement : PreparedStatement<MySqlDbType>,
        command : MySqlCommand,
        reader : MySqlDataReader
    ) =

    let ctor =
        match statement.ResultConstructionInfo with
        | Some ctor -> ctor
        | None -> failwith "no ctorinfor generated"
    let innerEnumerable = lazy (DataReader.read reader ctor :?> IEnumerable<'T>)
    let innerEnumerator = lazy innerEnumerable.Force().GetEnumerator()

    interface IEnumerator<'T> with
        member this.Current = innerEnumerator.Force().Current

    interface IEnumerator with
        member this.MoveNext() = innerEnumerator.Force().MoveNext()

        member this.Current = innerEnumerator.Force().Current :> obj

        member this.Reset() = innerEnumerator.Force().Reset()

    interface IAsyncEnumerator<'T> with
        // MySqlDataReader does not have any asynchronous capability
        member this.MoveNextAsync() = Task.FromResult(innerEnumerator.Force().MoveNext())

    interface IDisposable with
        member this.Dispose() =
            reader.Dispose()
            command.Dispose()
