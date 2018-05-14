namespace Invio.QueryProvider.MySql

open System
open System.Collections
open System.Collections.Generic
open System.Data
open System.Linq.Expressions

open MySql.Data.MySqlClient

open System.Linq
open System.Reflection
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

    let createCommand con expression =
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
        command :> System.Data.IDbCommand, ctor

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
        let cmd, ctorInfo = createCommand connection expression
        use reader = cmd.ExecuteReader()
        DataReader.read reader ctorInfo

    override this.PrepareEnumerable expression =
        let prepareEnumerable = prepareEnumerableMethod.MakeGenericMethod(unwrapQueryableType <| expression.Type)
        let args : obj array = [| expression |]
        prepareEnumerable.Invoke(this, args) :?> IEnumerable

    member internal this.PrepareEnumerable<'T> (expression : Expression) : IEnumerable<'T> =
        let statement = translateToStatement None None None expression

        SqlCommandEnumerable<'T>(connection, statement) :> IEnumerable<'T>

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

    let command = lazy createCommand connection statement
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
