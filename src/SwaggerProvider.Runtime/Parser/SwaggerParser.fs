namespace Swagger.Parser
open Swagger.Parser.Schema

module internal JsonAdapter =
    open Swagger.Parser.Exceptions
    open Newtonsoft.Json.Linq

    /// Schema node for Swagger schemes in Json format
    type JsonNodeAdapter(value:JToken) =
        inherit SchemaNode()

        override __.AsBoolean() = value.ToObject<bool>()

        override __.AsString() = value.ToObject<string>()

        override __.AsArray() =
            match value.Type with
            | JTokenType.Array -> 
                value :?> JArray
                |> Seq.map (fun x -> JsonNodeAdapter(x) :> SchemaNode)
                |> Seq.toArray
            | _ -> raise <| UnexpectedValueTypeException(value, "string")

        override __.AsStringArrayWithoutNull() =
            match value.Type with
            | JTokenType.String -> 
                [|value.ToObject<string>()|]
            | JTokenType.Array ->
                value :?> JArray
                |> Seq.map (fun x -> x.ToObject<string>())
                |> Seq.filter (fun x -> x <> "null")
                |> Seq.toArray
            | other -> 
                failwithf "Value: '%A' cannot be converted to StringArray" other

        override __.Properties() =
            match value.Type with
            | JTokenType.Object -> 
                (value :?> JObject).Properties()
                |> Seq.map (fun x -> x.Name, JsonNodeAdapter(x.Value) :> SchemaNode)
                |> Seq.toArray
            | _ -> raise <| UnexpectedValueTypeException(value, "JObject")

        override __.TryGetProperty(property) =
            match value.Type with
            | JTokenType.Object -> 
                let obj = value :?> JObject
                match obj.TryGetValue(property) with
                | true, x -> Some(JsonNodeAdapter(x) :> SchemaNode)
                | _ -> None
            | _ -> None

    let parse = JToken.Parse >> JsonNodeAdapter

module internal YamlAdapter =
    open Swagger.Parser.Exceptions
    open System.IO
    open YamlDotNet.Serialization
    open System.Collections.Generic

    let (|List|_|) (node: obj) =
        match node with
        | :? List<obj> as l -> Some l
        | _ -> None

    let (|Map|_|) (node: obj) =
        match node with
        | :? Dictionary<obj,obj> as dict ->
            dict 
            |> Seq.choose (fun p ->
                match p.Key with
                | :? string as key -> Some (key, p.Value)
                | _ -> None)
            |> Some
        | _ -> None

    let (|Scalar|_|) (node: obj) =
        match node with
        | :? List<obj> 
        | :? Dictionary<obj,obj> ->
            None
        | scalar -> 
            let value = if isNull scalar then "" else scalar.ToString()
            Some (value)
            
    /// SchemaNode for Swagger schemes in Yaml format
    type YamlNodeAdapter(value:obj) =
        inherit SchemaNode()

        override __.AsBoolean() =
            match value with
            | Scalar(x) -> System.Boolean.Parse(x)
            | _ -> raise <| UnexpectedValueTypeException(value, "bool")

        override __.AsString() =
            match value with
            | Scalar(x) -> x
            | _ -> raise <| UnexpectedValueTypeException(value, "string")

        override __.AsArray() =
            match value with
            | List(nodes) ->
                nodes |> Seq.map(fun x->YamlNodeAdapter(x) :> SchemaNode) |> Array.ofSeq
            | _ -> [||]

        override __.AsStringArrayWithoutNull() =
            match value with
            | Scalar(x) -> [|x|]
            | List(nodes) ->
                nodes 
                |> Seq.map(function
                    | Scalar (x) -> x
                    | x -> failwithf "'%A' cannot be converted to string" x)
                |> Seq.filter (fun x -> x <> "null")
                |> Seq.toArray
            | other -> failwithf "Value: '%A' cannot be converted to StringArray" other

        override __.Properties() =
            match value with
            | Map(pairs) -> pairs |> Seq.map (fun (a,b)-> (a, YamlNodeAdapter(b) :> SchemaNode)) |> Array.ofSeq
            | _ -> raise <| UnexpectedValueTypeException(value, "map")

        override __.TryGetProperty(prop) =
            match value with
            | Map(items) ->
                items
                |> Seq.tryFind (fst >> ((=) prop))
                |> Option.map (fun (_,x) -> YamlNodeAdapter(x) :> SchemaNode)
            | _ -> None

    let private deserializer = Deserializer()
    let parse (text:string) =
        try
            use reader = new StringReader(text)
            deserializer.Deserialize(reader) |> YamlNodeAdapter
        with
        | :? YamlDotNet.Core.YamlException as e when not <| isNull e.InnerException ->
            raise e.InnerException // inner exceptions are much more informative
        | _ -> reraise()

module SwaggerParser =

    let parseJson schema =
        (JsonAdapter.parse schema) :> SchemaNode

    let parseYaml schema =
        (YamlAdapter.parse schema) :> SchemaNode

    let parseSchema (schema:string) : SwaggerObject =
        let parse = 
            if schema.Trim().StartsWith("{")
            then parseJson else parseYaml
        parse schema  |> Parsers.parseSwaggerObject