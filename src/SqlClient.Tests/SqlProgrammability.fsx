#r "../../bin/Fsharp.Data.SqlClient.dll"
#r "../../bin/Microsoft.SqlServer.Types.dll"
#load "ConnectionStrings.fs"
open System
open System.Data
open FSharp.Data


[<Literal>] 
//let connectionString = ConnectionStrings.AdventureWorksLiteral
let connectionString = ConnectionStrings.AdventureWorksAzure

[<Literal>] 
let prodConnectionString = ConnectionStrings.MasterDb

type AdventureWorks2012 = SqlProgrammabilityProvider<connectionString>
type dbo = AdventureWorks2012.dbo
type HumanResources = AdventureWorks2012.HumanResources
type Person = AdventureWorks2012.Person
type Production = AdventureWorks2012.Production
type Purchasing = AdventureWorks2012.Purchasing
type Sales = AdventureWorks2012.Sales

//Table-valued UDF selecting single row
type GetContactInformation = dbo.ufnGetContactInformation
let getContactInformation = new GetContactInformation()
getContactInformation.Execute(1) |> printfn "%A"
let f = getContactInformation.Execute( 1) |> Seq.exactlyOne
f.BusinessEntityType
f.FirstName
f.JobTitle
f.LastName
f.PersonID

//Scalar-Value
type LeadingZeros = dbo.ufnLeadingZeros
let leadingZeros = new LeadingZeros()
leadingZeros.Execute(12) 

//Stored Procedure returning list of records similar to SqlCommandProvider
type GetWhereUsedProductID = dbo.uspGetWhereUsedProductID
let getWhereUsedProductID = new GetWhereUsedProductID()
getWhereUsedProductID.AsyncExecute(1, DateTime(2013,1,1)) |> Async.RunSynchronously |> Array.ofSeq

//
//UDTT with nullable column
type myType = Person.``User-Defined Table Types``.MyTableType
let m = [ myType(myId = 2); myType(myId = 1) ]

type MyProc = Person.MyProc
let myArray = (new MyProc()).AsyncExecute(m) |> Async.RunSynchronously |> Array.ofSeq

let myRes = myArray.[0]
myRes.myId
myRes.myName

//Call stored procedure to update
type UpdateEmployeeLogin = AdventureWorks2012.HumanResources.uspUpdateEmployeeLogin
let updateEmployeeLogin = new UpdateEmployeeLogin()

let res = updateEmployeeLogin.AsyncExecute(
                291, 
                Microsoft.SqlServer.Types.SqlHierarchyId.Parse(SqlTypes.SqlString("/1/4/2/")),
                "adventure-works\gat0", 
                "gatekeeper", 
                DateTime(2013,1,1), 
                true 
            )
            |> Async.RunSynchronously 


dbo.ufnGetStock2.Create().Execute(Some 1) //324
dbo.ufnGetStock2.Create().Execute() //83173

dbo.Echo.Create().ExecuteSingle 42
dbo.Echo.Create().ExecuteSingle()


dbo.EchoText.Create().Execute()
dbo.EchoText.Create().Execute("Hello, world!")