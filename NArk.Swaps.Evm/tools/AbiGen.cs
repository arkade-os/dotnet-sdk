#:package Nethereum.Generators@6.0.0

// Generates Nethereum Function/Event C# DTOs from a compiled Solidity ABI JSON file.
// A .NET 10 file-based app (no .csproj/sln entry — just `dotnet run tools/AbiGen.cs -- ...`)
// so this dev-time codegen tool doesn't need its own project. Lives under tools/, excluded
// from NArk.Swaps.Evm.csproj's compile items (see the <Compile Remove> there) so the main
// project doesn't try to build it too. Used to (re)generate Contracts/Generated/ from
// Contracts/Sol/ERC20Swap.sol — see generate-bindings.sh for the full pipeline (solc compile
// + this tool).
//
// Usage: dotnet run tools/AbiGen.cs -- <abi.json> <output-dir> <namespace> [bin-file contract-name]
//
// The optional trailing pair (bin-file, contract-name) additionally generates a
// ContractDeploymentMessage class embedding the compiled bytecode — needed for test fixtures
// that deploy their own throwaway copy of a contract (e.g. TestERC20 on a local Anvil chain),
// as opposed to calling an already-deployed one (ERC20Swap on real Arbitrum, which is all the
// production provider needs).

using System.Text.Json;
using Nethereum.Generators.Core;
using Nethereum.Generators.CQS;
using Nethereum.Generators.DTOs;
using Nethereum.Generators.Model;

if (args.Length < 3)
{
    Console.Error.WriteLine("Usage: AbiGen <abi.json> <output-dir> <namespace> [bin-file contract-name]");
    return 1;
}

var abiPath = args[0];
var outDir = args[1];
var ns = args[2];
var binPath = args.Length > 3 ? args[3] : null;
var contractName = args.Length > 4 ? args[4] : null;

if (Directory.Exists(outDir))
    Directory.Delete(outDir, recursive: true);
Directory.CreateDirectory(outDir);

using var doc = JsonDocument.Parse(File.ReadAllText(abiPath));

var contractAbi = new ContractABI();
var allFunctions = new List<FunctionABI>();
var allEvents = new List<EventABI>();
var allStructs = new List<StructABI>();
var structNamesSeen = new HashSet<string>();
ConstructorABI? constructorAbi = null;

// Solidity struct params (ABI type "tuple"/"tuple[]") carry their field list in "components"
// and a Solidity-side name in "internalType" (e.g. "struct ERC20Swap.BatchClaimEntry[]").
// Nethereum's generator wants these as a separate StructABI + a matching ParameterABI.StructType
// on the referencing parameter, not inlined.
string RegisterStruct(JsonElement p)
{
    var internalType = p.GetProperty("internalType").GetString()!;
    var structName = internalType.Split('.').Last().TrimEnd('[', ']');

    if (structNamesSeen.Add(structName))
    {
        var members = BuildParams(p.GetProperty("components"), withIndexed: false);
        allStructs.Add(new StructABI(structName) { InputParameters = members });
    }

    return structName;
}

ParameterABI[] BuildParams(JsonElement arr, bool withIndexed)
{
    var list = new List<ParameterABI>();
    var order = 1;
    foreach (var p in arr.EnumerateArray())
    {
        var type = p.GetProperty("type").GetString()!;
        var name = p.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        var structType = type.StartsWith("tuple") ? RegisterStruct(p) : null;
        var param = new ParameterABI(type, name, order++, structType);
        if (withIndexed && p.TryGetProperty("indexed", out var idx))
            param.Indexed = idx.GetBoolean();
        list.Add(param);
    }
    return list.ToArray();
}

// First pass: build all FunctionABI/EventABI against the shared ContractABI (needed for
// overload-name resolution — Nethereum's generator disambiguates same-named overloads by
// looking up siblings via the ContractABI back-reference), then populate
// ContractABI.Functions/Events, THEN generate.
foreach (var entry in doc.RootElement.EnumerateArray())
{
    var type = entry.GetProperty("type").GetString();
    if (type == "function")
    {
        var name = entry.GetProperty("name").GetString()!;
        var stateMutability = entry.TryGetProperty("stateMutability", out var sm) ? sm.GetString() : null;
        var constant = stateMutability is "view" or "pure";
        var inputs = BuildParams(entry.GetProperty("inputs"), withIndexed: false);
        var outputs = entry.TryGetProperty("outputs", out var outs) ? BuildParams(outs, withIndexed: false) : [];

        allFunctions.Add(new FunctionABI(name, constant, contractAbi, false)
        {
            InputParameters = inputs,
            OutputParameters = outputs,
        });
    }
    else if (type == "event")
    {
        var name = entry.GetProperty("name").GetString()!;
        var inputs = BuildParams(entry.GetProperty("inputs"), withIndexed: true);
        allEvents.Add(new EventABI(name, contractAbi) { InputParameters = inputs });
    }
    else if (type == "constructor")
    {
        var inputs = BuildParams(entry.GetProperty("inputs"), withIndexed: false);
        constructorAbi = new ConstructorABI { InputParameters = inputs };
    }
}

contractAbi.Functions = allFunctions.ToArray();
contractAbi.Events = allEvents.ToArray();
contractAbi.Structs = allStructs.ToArray();

foreach (var structAbi in allStructs)
{
    var structGen = new StructTypeGenerator(structAbi, ns, CodeGenLanguage.CSharp, []);
    File.WriteAllText(Path.Combine(outDir, $"{structAbi.Name}.cs"), Wrap(structGen.GenerateClass()));
}

foreach (var functionAbi in allFunctions)
{
    var cqsGen = new FunctionCQSMessageGenerator(functionAbi, ns, ns, ns, CodeGenLanguage.CSharp, "");
    var suffix = functionAbi.InputParameters.Length;
    File.WriteAllText(Path.Combine(outDir, $"{functionAbi.Name}Function_{suffix}.cs"), Wrap(cqsGen.GenerateClass()));

    if (functionAbi.OutputParameters.Length > 0)
    {
        var outGen = new FunctionOutputDTOGenerator(functionAbi, ns, ns, CodeGenLanguage.CSharp);
        File.WriteAllText(Path.Combine(outDir, $"{functionAbi.Name}Output_{suffix}.cs"), Wrap(outGen.GenerateClass()));
    }
}

foreach (var eventAbi in allEvents)
{
    var eventGen = new EventDTOGenerator(eventAbi, ns, ns, CodeGenLanguage.CSharp);
    File.WriteAllText(Path.Combine(outDir, $"{eventAbi.Name}EventDTO.cs"), Wrap(eventGen.GenerateClass()));
}

if (binPath != null && contractName != null)
{
    var byteCode = "0x" + File.ReadAllText(binPath).Trim();
    var deployAbi = constructorAbi ?? new ConstructorABI { InputParameters = [] };
    var deployGen = new ContractDeploymentCQSMessageGenerator(deployAbi, ns, byteCode, contractName, CodeGenLanguage.CSharp);
    File.WriteAllText(Path.Combine(outDir, $"{contractName}Deployment.cs"), Wrap(deployGen.GenerateClass()));
    Console.WriteLine($"Generated {contractName}Deployment.cs (embedded bytecode from {binPath})");
}

Console.WriteLine($"Generated {allFunctions.Count} function DTO(s) and {allEvents.Count} event DTO(s) into {outDir}");
return 0;

string Wrap(string classBody) => $$"""
// <auto-generated>
//     This file was generated by AbiGen.cs from a compiled Solidity ABI.
//     Do not edit by hand — re-run generate-bindings.sh instead.
// </auto-generated>
// CS1591 (missing XML doc comment): generated members are self-descriptive from the ABI
// (parameter names/types); hand-adding doc comments here would just be re-generated away.
#pragma warning disable CS1591
using System;
using System.Numerics;
using System.Threading.Tasks;
using System.Collections.Generic;
using Nethereum.Contracts;
using Nethereum.ABI.FunctionEncoding.Attributes;

namespace {{ns}}
{
{{classBody}}
}

""";
