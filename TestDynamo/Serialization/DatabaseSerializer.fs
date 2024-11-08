namespace TestDynamo.Serialization

open System.Runtime.InteropServices
open System.Text.RegularExpressions
open System.Threading.Tasks
open TestDynamo.Api.FSharp
open TestDynamo.Data
open TestDynamo.Data.BasicStructures
open TestDynamo.Model
open TestDynamo.Serialization.Data
open TestDynamo.Utils
open System.IO
open System.Text.Json
open System.Threading
open TestDynamo
open TestDynamo.Data.Monads.Operators
open Microsoft.Extensions.Logging

type VersionedData<'a>(
    data: 'a,
    version: string) =
    
    static member currentVersion = "1"
    
    member _.version = version
    member _.data = data

module private BaseSerializer =
    
    let options =
        let opts () =
            let options = JsonSerializerOptions()
            Converters.customConverters
            |> List.fold (options.Converters.Add |> asLazy) ()
            
            options.IncludeFields <- true
            options
        
        let options = opts()
        let indentOptions = opts()
        indentOptions.WriteIndented <- true
        
        fun indent -> if indent then indentOptions else options
    
    module Strings =
    
        let write indent data =
            let opts = options indent
            JsonSerializer.Serialize(VersionedData<_>(data, VersionedData<_>.currentVersion), opts)
            
        let read<'a> (json: string) =
            let opts = options false
            JsonSerializer.Deserialize<VersionedData<'a>>(json, opts).data
            
    module Streams =
        
        let write indent stream data =
            let opts = options indent
            JsonSerializer.Serialize(utf8Json = stream, options = opts, value = VersionedData<_>(data, VersionedData<_>.currentVersion))
            
        let read<'a> (stream: Stream) =
            let opts = options false
            JsonSerializer.Deserialize<VersionedData<'a>>(utf8Json = stream, options = opts).data
    
        let create indent data =
            let ms = new MemoryStream()
            write indent ms data
            ms.Position <- 0
            ms
            
    module StreamsAsync =
            
        let write indent stream c data =
            let opts = options indent
            JsonSerializer.SerializeAsync(utf8Json = stream, value = VersionedData<_>(data, VersionedData<_>.currentVersion), options = opts, cancellationToken = c)
            
        let read<'a> (stream: Stream) c =
            let opts = options false
            JsonSerializer.DeserializeAsync<VersionedData<'a>>(stream, opts, c) |%|> _.data
    
        let create indent c data =
            let ms = new MemoryStream()
            write indent ms c data 
            |> ValueTask
            |> Io.normalizeVt
            |%|> fun _ ->
                ms.Position <- 0
                ms

module DatabaseSerializer =
        
    let private toString = BaseSerializer.Strings.write
        
    let private toStream = BaseSerializer.Streams.write
        
    let private toStreamAsync indent c =
        flip (BaseSerializer.StreamsAsync.write indent) c
        >>> ValueTask
        
    let private toFile indent file data =
        use file = File.OpenWrite file
        BaseSerializer.Streams.write indent file data
        
    let private toFileAsync indent c file data =
        ValueTask<_>(task = task {
            use file = File.OpenWrite file
            return! toStreamAsync indent c file data
        })
        
    let private createStream = BaseSerializer.Streams.create
        
    let private createStreamAsync =
        BaseSerializer.StreamsAsync.create
    
    let private fromString = BaseSerializer.Strings.read
        
    let private fromStream = BaseSerializer.Streams.read
        
    let private fromStreamAsync c str =
        BaseSerializer.StreamsAsync.read str c
        
    let private fromFile file =
        use file = File.OpenRead file
        BaseSerializer.Streams.read file
        
    let private fromFileAsync c file =
        ValueTask<_>(task = task {
            use file = File.OpenRead file
            return! fromStreamAsync c file
        })
    
    type Serializer<'a, 'ser>(toSerializable: bool -> 'a -> 'ser, fromSerializable: ILogger voption -> 'ser -> 'a) =
                
        member _.ToString(
            data,
            [<Optional; DefaultParameterValue(false)>] schemaOnly,
            [<Optional; DefaultParameterValue(false)>] indent) = toSerializable schemaOnly data |> toString indent
        member _.FromString(json, [<Optional; DefaultParameterValue(null: ILogger)>] logger) = fromString json |> fromSerializable (CSharp.toOption logger)
        
        member _.ToStream(
            data,
            [<Optional; DefaultParameterValue(false)>]schemaOnly,
            [<Optional; DefaultParameterValue(false)>]indent) = toSerializable schemaOnly data |> createStream indent
        member _.ToStreamAsync(
            data,
            [<Optional; DefaultParameterValue(false)>]schemaOnly,
            [<Optional; DefaultParameterValue(false)>]indent,
            [<Optional; DefaultParameterValue(CancellationToken())>]c) = toSerializable schemaOnly data |> createStreamAsync indent c
        
        member _.WriteToStream(
            data,
            stream,
            [<Optional; DefaultParameterValue(false)>]schemaOnly,
            [<Optional; DefaultParameterValue(false)>]indent) = toSerializable schemaOnly data |> toStream indent stream
        member _.WriteToStreamAsync(
            data,
            stream,
            [<Optional; DefaultParameterValue(false)>]schemaOnly,
            [<Optional; DefaultParameterValue(false)>]indent,
            [<Optional; DefaultParameterValue(CancellationToken())>]c) = toSerializable schemaOnly data |> toStreamAsync indent c stream
        member _.ToFile(
            data,
            file,
            [<Optional; DefaultParameterValue(false)>]schemaOnly,
            [<Optional; DefaultParameterValue(false)>]indent) = toSerializable schemaOnly data |> toFile indent file
        member _.ToFileAsync(
            data,
            file,
            [<Optional; DefaultParameterValue(false)>]schemaOnly,
            [<Optional; DefaultParameterValue(false)>]indent,
            [<Optional; DefaultParameterValue(CancellationToken())>]c) = toSerializable schemaOnly data |> toFileAsync indent c file
        
        member _.FromStream(json, [<Optional; DefaultParameterValue(null: ILogger)>] logger) = fromStream json |> fromSerializable (CSharp.toOption logger)
        member _.FromStreamAsync(
            json,
            [<Optional; DefaultParameterValue(null: ILogger)>] logger,
            [<Optional; DefaultParameterValue(CancellationToken())>] c) = fromStreamAsync c json |%|> fromSerializable (CSharp.toOption logger)
        
        member _.FromFile(file, [<Optional; DefaultParameterValue(null: ILogger)>] logger) = fromFile file |> fromSerializable (CSharp.toOption logger)
        member _.FromFileAsync(
            file,
            [<Optional; DefaultParameterValue(null: ILogger)>] logger,
            [<Optional; DefaultParameterValue(CancellationToken())>] c) = fromFileAsync c file |%|> fromSerializable (CSharp.toOption logger)
    
    let Database = Serializer<Api.FSharp.Database, Version1.SerializableDatabase>(Version1.ToSerializable.Database.toSerializable [||], Version1.FromSerializable.Database.fromSerializable)
    let GlobalDatabase = Serializer<Api.FSharp.GlobalDatabase, Version1.SerializableGlobalDatabase>(Version1.ToSerializable.GlobalDatabase.toSerializable, Version1.FromSerializable.GlobalDatabase.fromSerializable)