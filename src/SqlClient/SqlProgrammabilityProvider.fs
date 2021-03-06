﻿namespace FSharp.Data

open System
open System.Collections.Generic
open System.Data
open System.Data.SqlClient
open System.Diagnostics
open System.IO
open System.Reflection
open System.Runtime.Caching
open System.Data.SqlTypes

open Microsoft.FSharp.Core.CompilerServices
open Microsoft.FSharp.Quotations

open Microsoft.SqlServer.Server

open ProviderImplementation.ProvidedTypes

open FSharp.Data.SqlClient

[<TypeProvider>]
type public SqlProgrammabilityProvider(config : TypeProviderConfig) as this = 
    inherit TypeProviderForNamespaces()

    let assembly = Assembly.LoadFrom( config.RuntimeAssembly)
    let nameSpace = this.GetType().Namespace
    let providerType = ProvidedTypeDefinition(assembly, nameSpace, "SqlProgrammabilityProvider", Some typeof<obj>, HideObjectMethods = true)

    let cache = new MemoryCache(name = this.GetType().Name)

    do 
        this.Disposing.Add <| fun _ -> 
            cache.Dispose()
            dataTypeMappings.Clear()
    do 
        //this.RegisterRuntimeAssemblyLocationAsProbingFolder( config) 

        providerType.DefineStaticParameters(
            parameters = [ 
                ProvidedStaticParameter("ConnectionStringOrName", typeof<string>) 
                ProvidedStaticParameter("ResultType", typeof<ResultType>, ResultType.Records) 
                ProvidedStaticParameter("ConfigFile", typeof<string>, "") 
                ProvidedStaticParameter("DataDirectory", typeof<string>, "") 
            ],             
            instantiationFunction = (fun typeName args ->
                cache.GetOrAdd(typeName, lazy this.CreateRootType(typeName, unbox args.[0], unbox args.[1], unbox args.[2], unbox args.[3]))
            ) 
        )

        providerType.AddXmlDoc """
<summary>Typed access to SQL Server programmable objects: stored procedures, functions and user defined table types.</summary> 
<param name='ConnectionStringOrName'>String used to open a SQL Server database or the name of the connection string in the configuration file in the form of “name=&lt;connection string name&gt;”.</param>
<param name='ResultType'>A value that defines structure of result: Records, Tuples, DataTable, or SqlDataReader.</param>
<param name='ConfigFile'>The name of the configuration file that’s used for connection strings at DESIGN-TIME. The default value is app.config or web.config.</param>
<param name='DataDirectory'>The name of the data directory that replaces |DataDirectory| in connection strings. The default value is the project or script directory.</param>
"""

        this.AddNamespace(nameSpace, [ providerType ])
    
    override this.ResolveAssembly args = 
        match config.ReferencedAssemblies |> Array.tryFind (fun x -> AssemblyName.ReferenceMatchesDefinition(AssemblyName.GetAssemblyName x, AssemblyName args.Name)) with
        | Some x -> Assembly.LoadFrom x
        | None -> base.ResolveAssembly args

    member internal this.CreateRootType( typeName, connectionStringOrName, resultType, configFile, dataDirectory) =
        if String.IsNullOrWhiteSpace connectionStringOrName then invalidArg "ConnectionStringOrName" "Value is empty!" 
        
        let connectionStringName, isByName = Configuration.ParseConnectionStringName connectionStringOrName

        let designTimeConnectionString = 
            if isByName 
            then Configuration.ReadConnectionStringFromConfigFileByName(connectionStringName, config.ResolutionFolder, configFile)
            else connectionStringOrName

        let dataDirectoryFullPath = 
            if dataDirectory = "" then  config.ResolutionFolder
            elif Path.IsPathRooted dataDirectory then dataDirectory
            else Path.Combine (config.ResolutionFolder, dataDirectory)

        AppDomain.CurrentDomain.SetData("DataDirectory", dataDirectoryFullPath)

        let conn = new SqlConnection(designTimeConnectionString)
        use closeConn = conn.UseLocally()
        conn.CheckVersion()
        conn.LoadDataTypesMap()

        let databaseRootType = ProvidedTypeDefinition(assembly, nameSpace, typeName, baseType = Some typeof<obj>, HideObjectMethods = true)

        let tagProvidedType(t: ProvidedTypeDefinition) =
            t.AddMember(ProvidedProperty("ConnectionStringOrName", typeof<string>, [], IsStatic = true, GetterCode = fun _ -> <@@ connectionStringOrName @@>))

        databaseRootType.AddMembersDelayed <| fun () ->
            conn.GetUserSchemas() 
            |> List.map (fun schema ->
                let schemaRoot = ProvidedTypeDefinition(schema, baseType = Some typeof<obj>, HideObjectMethods = true)
                schemaRoot.AddMembersDelayed <| fun() -> 
                    [
                        let udtts = this.UDTTs (conn.ConnectionString, schema)
                        udtts |> List.iter tagProvidedType
                        let udttsRoot = ProvidedTypeDefinition("User-Defined Table Types", Some typeof<obj>)
                        udttsRoot.AddMembers udtts
                        yield udttsRoot

                        let routines = this.Routines(conn, schema, udtts, resultType, isByName, connectionStringName, connectionStringOrName)
                        routines |> List.iter tagProvidedType
                        yield! routines

                        yield this.Tables(conn, schema, isByName, connectionStringName, connectionStringOrName, tagProvidedType)
                    ]
                schemaRoot            
            )

        databaseRootType           

     member internal __.UDTTs( connStr, schema) = [
        for t in dataTypeMappings.[connStr] do
            if t.TableType && t.Schema = schema
            then 
                let rowType = ProvidedTypeDefinition(t.UdttName, Some typeof<obj>, HideObjectMethods = true)
                    
                let parameters = [ 
                    for p in t.TableTypeColumns.Value -> 
                        ProvidedParameter(p.Name, p.ClrTypeConsideringNullable, ?optionalValue = if p.Nullable then Some null else None) 
                ] 

                let ctor = ProvidedConstructor( parameters)
                ctor.InvokeCode <- fun args -> 
                    let optionsToNulls = QuotationsFactory.MapArrayNullableItems(List.ofArray t.TableTypeColumns.Value, "MapArrayOptionItemToObj") 
                    <@@
                        let values: obj[] = %%Expr.NewArray(typeof<obj>, [ for a in args -> Expr.Coerce(a, typeof<obj>) ])
                        (%%optionsToNulls) values
                        values
                    @@>

                rowType.AddMember ctor
                rowType.AddXmlDoc "User-Defined Table Type"
                yield rowType
    ]

    member internal __.Routines(conn, schema, udtts, resultType, isByName, connectionStringName, connectionStringOrName) = 
        [
            use _ = conn.UseLocally()
            let routines = conn.GetRoutines( schema) 
            for routine in routines do
             
                let cmdProvidedType = ProvidedTypeDefinition(routine.Name, Some typeof<``ISqlCommand Implementation``>, HideObjectMethods = true)
                cmdProvidedType.AddXmlDoc <| 
                    match routine with 
                    | StoredProcedure _ -> "Stored Procedure"
                    | TableValuedFunction _ -> "Table-Valued Function"
                    | ScalarValuedFunction _ -> "Scalar-Valued Function"
                
                cmdProvidedType.AddMembersDelayed <| fun() ->
                //cmdProvidedType.AddMembers
                    [
                        use __ = conn.UseLocally()
                        let parameters = conn.GetParameters( routine)

                        let commandText = routine.CommantText(parameters)
                        let outputColumns = 
                            if resultType <> ResultType.DataReader
                            then 
                                DesignTime.GetOutputColumns(conn, commandText, parameters, routine.IsStoredProc)
                            else 
                                []

                        let rank = match routine with ScalarValuedFunction _ -> ResultRank.ScalarValue | _ -> ResultRank.Sequence
                        let output = DesignTime.GetOutputTypes(outputColumns, resultType, rank)
        
                        do  //Record
                            output.ProvidedRowType |> Option.iter cmdProvidedType.AddMember

                        //ctors
                        let sqlParameters = Expr.NewArray( typeof<SqlParameter>, parameters |> List.map QuotationsFactory.ToSqlParam)
                        let rank = match routine with | ScalarValuedFunction _ -> ResultRank.ScalarValue | _ -> ResultRank.Sequence
                        let ctorArgsExceptConnection = [
                            Expr.Value commandText           
                            Expr.Value(routine.IsStoredProc) 
                            sqlParameters                               
                            Expr.Value resultType                       
                            Expr.Value rank
                            output.RowMapping                           
                            Expr.Value output.ErasedToRowType.PartialAssemblyQualifiedName
                        ]

                        let ctorImpl = typeof<``ISqlCommand Implementation``>.GetConstructors() |> Seq.exactlyOne
                        
                        //default ctor and create factory 
                        let ctor1Params = 
                            [ 
                                ProvidedParameter("connectionString", typeof<string>, optionalValue = "") 
                                ProvidedParameter("commandTimeout", typeof<int>, optionalValue = SqlCommand.DefaultTimeout) 
                            ]

                        let ctor1Body(args: _ list) = 
                            let connArg =
                                <@@ 
                                    if not( String.IsNullOrEmpty(%%args.[0])) then Connection.Literal %%args.[0] 
                                    elif isByName then Connection.NameInConfig connectionStringName
                                    else Connection.Literal connectionStringOrName
                                @@>
                            Expr.NewObject(ctorImpl, connArg :: args.[1] :: ctorArgsExceptConnection)

                        yield ProvidedConstructor(ctor1Params, InvokeCode = ctor1Body) :> MemberInfo
                        yield upcast ProvidedMethod("Create", ctor1Params, returnType = cmdProvidedType, IsStaticMethod = true, InvokeCode = ctor1Body) 
                           
                        //ctor and create factory with explicit connection/transaction support
                        let ctor2Params = 
                            [ 
                                ProvidedParameter("connection", typeof<SqlConnection>)
                                ProvidedParameter("transaction", typeof<SqlTransaction>, optionalValue = null) 
                                ProvidedParameter("commandTimeout", typeof<int>, optionalValue = SqlCommand.DefaultTimeout) 
                            ]

                        let ctor2Body (args: _ list) = 
                            Expr.NewObject(ctorImpl, <@@ Connection.``Connection and-or Transaction``(%%args.[0], %%args.[1]) @@> :: args.[2] :: ctorArgsExceptConnection)
                    
                        yield upcast ProvidedConstructor(ctor2Params, InvokeCode = ctor2Body)
                        yield upcast ProvidedMethod("Create", ctor2Params, returnType = cmdProvidedType, IsStaticMethod = true, InvokeCode = ctor2Body)

                        let executeArgs = DesignTime.GetExecuteArgs(cmdProvidedType, parameters, udtts)

                        yield upcast DesignTime.AddGeneratedMethod(parameters, executeArgs, cmdProvidedType.BaseType, output.ProvidedType, "Execute") 
                            
                        let asyncReturnType = ProvidedTypeBuilder.MakeGenericType(typedefof<_ Async>, [ output.ProvidedType ])
                        yield upcast DesignTime.AddGeneratedMethod(parameters, executeArgs, cmdProvidedType.BaseType, asyncReturnType, "AsyncExecute")

                        if output.ErasedToRowType <> typeof<Void>
                        then 
                            let providedReturnType = 
                                ProvidedTypeBuilder.MakeGenericType(
                                    typedefof<_ option>, 
                                    [ (match output.ProvidedRowType with None -> output.ErasedToRowType | Some x -> upcast x)  ]
                                ) 

                            let providedAsyncReturnType = 
                                ProvidedTypeBuilder.MakeGenericType(
                                    typedefof<_ Async>, 
                                    [ providedReturnType ]
                                ) 

                            yield upcast DesignTime.AddGeneratedMethod(parameters, executeArgs, cmdProvidedType.BaseType, providedReturnType, "ExecuteSingle") 
                            yield upcast DesignTime.AddGeneratedMethod(parameters, executeArgs, cmdProvidedType.BaseType, providedAsyncReturnType, "AsyncExecuteSingle")
                    ]

                yield cmdProvidedType
        ]

    member internal __.Tables(conn: SqlConnection, schema, isByName, connectionStringName, connectionString, tagProvidedType) = 
        let tables = ProvidedTypeDefinition("Tables", Some typeof<obj>)
        //tagProvidedType tables
        tables.AddMembersDelayed <| fun() ->
            use __ = conn.UseLocally()
            let isSqlAzure = conn.IsSqlAzure
            conn.GetTables(schema)
            |> List.map (fun tableName -> 

                let twoPartTableName = sprintf "[%s].[%s]" schema tableName 

                let descriptionSelector = 
                    if isSqlAzure 
                    then "(SELECT NULL AS Value)"
                    else "fn_listextendedproperty ('MS_Description', 'schema', @schema, 'table', @tableName, 'column', columns.name)"

                let query = 
                    sprintf "
                        SELECT 
	                        columns.name
	                        ,columns.system_type_id
	                        ,columns.user_type_id
	                        ,columns.max_length
	                        ,columns.is_nullable
	                        ,is_identity AS is_identity_column
	                        ,is_updateable = CONVERT(BIT, CASE WHEN is_identity = 0 AND is_computed = 0 THEN 1 ELSE 0 END) 
	                        ,is_part_of_unique_key = CONVERT(BIT, CASE WHEN index_columns.object_id IS NULL THEN 0 ELSE 1 END)
	                        ,default_constraint = ISNULL(OBJECT_DEFINITION(default_object_id), '')
	                        ,description = ISNULL(XProp.Value, '')
                        FROM 
	                        sys.schemas 
	                        JOIN sys.tables ON 
		                        tables.schema_id = schemas.schema_id 
		                        AND schemas.name = @schema 
		                        AND tables.name = @tableName
	                        JOIN sys.columns ON columns.object_id = tables.object_id
	                        LEFT JOIN sys.indexes ON 
		                        tables.object_id = indexes.object_id 
		                        AND indexes.is_primary_key = 1
	                        LEFT JOIN sys.index_columns ON 
		                        index_columns.object_id = tables.object_id 
		                        AND index_columns.index_id = indexes.index_id 
		                        AND columns.column_id = index_columns.column_id
                            OUTER APPLY %s AS XProp
                        ORDER BY 
                            columns.column_id
                        "  descriptionSelector

                let cmd = new SqlCommand(query, conn)
                cmd.Parameters.AddWithValue("@tableName", tableName) |> ignore
                cmd.Parameters.AddWithValue("@schema", schema) |> ignore

                let columns =  
                    cmd.ExecuteQuery( fun x ->
                        let c = Column.Parse(x, fun(system_type_id, user_type_id) -> findTypeInfoBySqlEngineTypeId(conn.ConnectionString, system_type_id, user_type_id)) 
                        { c with DefaultConstraint = string x.["default_constraint"]; Description = string x.["description"]}
                    )
                    |> Seq.toArray


                //type data row
                let dataRowType = ProvidedTypeDefinition("Row", Some typeof<DataRow>)
                do 
                    for c in columns do
                        let property = 
                            let name, dataType = c.Name, c.TypeInfo.ClrType
                            if c.Nullable 
                            then
                                let propertType = typedefof<_ option>.MakeGenericType dataType
                                let property = ProvidedProperty(name, propertType, GetterCode = QuotationsFactory.GetBody("GetNullableValueFromDataRow", dataType, name))
                                
                                if not c.ReadOnly
                                then 
                                    property.SetterCode <- QuotationsFactory.GetBody("SetNullableValueInDataRow", dataType, name)
                                
                                property
                            else
                                let property = ProvidedProperty(name, dataType)
                                property.GetterCode <- 
                                    if c.Identity && c.TypeInfo.ClrType <> typeof<int>
                                    then
                                        fun args -> 
                                            <@@ 
                                                let value = (%%args.[0] : DataRow).[name]
                                                let targetType = Type.GetType(%%Expr.Value( c.TypeInfo.ClrTypeFullName), throwOnError = true)
                                                Convert.ChangeType(value, targetType)
                                            @@>
                                    else
                                        fun args -> <@@ (%%args.[0] : DataRow).[name] @@>
                                
                                if not c.ReadOnly
                                then 
                                    property.SetterCode <- fun args -> <@@ (%%args.[0] : DataRow).[name] <- %%Expr.Coerce(args.[1], typeof<obj>) @@>
                                
                                property

                        if c.Description <> "" 
                        then property.AddXmlDoc c.Description

                        dataRowType.AddMember property

                //type data table
                let dataTableType = ProvidedTypeDefinition(tableName, baseType = Some( typedefof<_ DataTable>.MakeGenericType(dataRowType)))
                tagProvidedType dataTableType
                dataTableType.AddMember dataRowType

                if not isSqlAzure
                then 
                    dataTableType.AddXmlDocDelayed <| fun() ->
                        use __ = conn.UseLocally()
                        let query = sprintf "SELECT value FROM fn_listextendedproperty ('MS_Description', 'schema', '%s', 'table', '%s', default, default)" schema tableName
                        let cmd = new SqlCommand(query, conn) 
                        cmd.ExecuteScalar() |> sprintf "<summary>%O</summary>"

                do //ctor
                    let ctor = ProvidedConstructor []
                    ctor.InvokeCode <- fun _ -> 
                        let serializedSchema = 
                            columns 
                            |> Array.map (fun x ->
                                let nullable = x.Nullable || x.HasDefaultConstraint
                                sprintf "%s\t%s\t%b\t%i\t%b\t%b\t%b" 
                                    x.Name x.TypeInfo.ClrTypeFullName nullable x.MaxLength x.ReadOnly x.Identity x.PartOfUniqueKey                                 
                            ) 
                            |> String.concat "\n"

                        <@@ 
                            let runTimeConnectionString = 
                                if isByName 
                                then Configuration.GetConnectionStringAtRunTime connectionStringName
                                else connectionString
                            let selectCommand = new SqlCommand()
                            selectCommand.CommandText <- "SELECT * FROM " + twoPartTableName
                            selectCommand.Connection <- 
                                new SqlConnection(runTimeConnectionString)

                            let table = new DataTable<DataRow>(twoPartTableName, selectCommand) 

                            let primaryKey = ResizeArray()
                            for line in serializedSchema.Split('\n') do
                                let xs = line.Split('\t')
                                let col = new DataColumn()
                                col.ColumnName <- xs.[0]
                                col.DataType <- Type.GetType( xs.[1], throwOnError = true)  
                                col.AllowDBNull <- Boolean.Parse xs.[2]
                                if col.DataType = typeof<string>
                                then 
                                    col.MaxLength <- int xs.[3]
                                col.ReadOnly <- Boolean.Parse xs.[4]
                                col.AutoIncrement <- Boolean.Parse xs.[5]
                                if Boolean.Parse xs.[6]
                                then    
                                    primaryKey.Add col 
                                table.Columns.Add col

                            table.PrimaryKey <- Array.ofSeq primaryKey

                            table
                        @@>
                    dataTableType.AddMember ctor
                
                do
                    let parameters, updateableColumns = 
                        [ 
                            for c in columns do 
                                if not(c.Identity || c.ReadOnly)
                                then 
                                    let dataType = c.TypeInfo.ClrType
                                    let parameter = 
                                        if c.NullableParameter
                                        then ProvidedParameter(c.Name, parameterType = typedefof<_ option>.MakeGenericType dataType, optionalValue = null)
                                        else ProvidedParameter(c.Name, dataType)

                                    yield parameter, c
                        ] 
                        |> List.sortBy (fun (_, c) -> c.NullableParameter) //move non-nullable params in front
                        |> List.unzip


                    let methodXmlDoc = 
                        String.concat "\n" [
                            for c in updateableColumns do
                                if c.Description <> "" 
                                then 
                                    yield sprintf "<param name='%s'>%O</param>" c.Name c.Description
                        ]
                        
                    let invokeCode = fun (args: _ list)-> 

                        let argsValuesConverted = 
                            (args.Tail, updateableColumns)
                            ||> List.map2 (fun valueExpr c ->
                                if c.NullableParameter
                                then 
                                    typeof<QuotationsFactory>
                                        .GetMethod("OptionToObj", BindingFlags.NonPublic ||| BindingFlags.Static)
                                        .MakeGenericMethod(c.TypeInfo.ClrType)
                                        .Invoke(null, [| box valueExpr |])
                                        |> unbox
                                else
                                    valueExpr
                            )

                        <@@ 
                            let table: DataTable<DataRow> = %%args.[0]
                            let row = table.NewRow()

                            let values: obj[] = %%Expr.NewArray(typeof<obj>, [ for x in argsValuesConverted -> Expr.Coerce(x, typeof<obj>) ])
                            let namesOfUpdateableColumns: string[] = %%Expr.NewArray(typeof<string>, [ for c in updateableColumns -> Expr.Value(c.Name) ])
                            let optionalParams: bool[] = %%Expr.NewArray(typeof<bool>, [ for c in updateableColumns -> Expr.Value(c.NullableParameter) ])

                            Debug.Assert(values.Length = namesOfUpdateableColumns.Length, "values.Length = namesOfUpdateableColumns.Length")
                            Debug.Assert(values.Length = optionalParams.Length, "values.Length = optionalParams.Length")

                            for name, value, optional in Array.zip3 namesOfUpdateableColumns values optionalParams do 
                                row.[name] <- if value = null && optional then box DbNull else value
                            row
                        @@>

                    do 
                        let newRowMethod = ProvidedMethod("NewRow", parameters, dataRowType, InvokeCode = invokeCode)
                        newRowMethod.AddXmlDoc methodXmlDoc
                        dataTableType.AddMember newRowMethod

                        let addRowMethod = ProvidedMethod("AddRow", parameters, typeof<Void>)
                        addRowMethod.AddXmlDoc methodXmlDoc
                        addRowMethod.InvokeCode <- fun args -> 
                            let newRow = invokeCode args
                            <@@
                                let table: DataTable<DataRow> = %%args.[0]
                                let row: DataRow = %%newRow
                                table.Rows.Add row
                            @@>
                        dataTableType.AddMember addRowMethod

                do //columns accessors
                    for c in columns do
                        let name = c.Name
                        dataTableType.AddMember <| 
                            ProvidedProperty(name + "Column", typeof<DataColumn>, [], GetterCode = fun args -> <@@ (%%Expr.Coerce(args.[0], typeof<DataTable>): DataTable).Columns.[name]  @@>)

                dataTableType
            )
        tables
        
