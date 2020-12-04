#pragma warning disable IDE0051

using Neo.IO;
using Neo.Ledger;
using Neo.Network.P2P.Payloads;
using Neo.Persistence;
using Neo.SmartContract.Manifest;
using System;
using System.Numerics;

namespace Neo.SmartContract.Native
{
    public sealed class ManagementContract : NativeContract
    {
        public override string Name => "Neo Contract Management";
        public override int Id => 0;
        public override uint ActiveBlockIndex => 0;

        private const byte Prefix_NextAvailableId = 15;

        private int GetNextAvailableId(StoreView snapshot)
        {
            StorageItem item = snapshot.Storages.GetAndChange(new KeyBuilder(Id, Prefix_NextAvailableId), () => new StorageItem(1));
            int value = (int)(BigInteger)item;
            item.Add(1);
            return value;
        }

        internal override void OnPersist(ApplicationEngine engine)
        {
            foreach (NativeContract contract in Contracts)
            {
                if (contract.ActiveBlockIndex != engine.Snapshot.PersistingBlock.Index)
                    continue;
                engine.Snapshot.Contracts.Add(contract.Hash, new ContractState
                {
                    Id = contract.Id,
                    Script = contract.Script,
                    Hash = contract.Hash,
                    Manifest = contract.Manifest
                });
                contract.Initialize(engine);
            }
        }

        [ContractMethod(0, CallFlags.AllowModifyStates)]
        private ContractState Deploy(ApplicationEngine engine, byte[] nefFile, byte[] manifest)
        {
            if (!(engine.ScriptContainer is Transaction tx))
                throw new InvalidOperationException();
            if (nefFile.Length == 0)
                throw new ArgumentException($"Invalid NefFile Length: {nefFile.Length}");
            if (manifest.Length == 0 || manifest.Length > ContractManifest.MaxLength)
                throw new ArgumentException($"Invalid Manifest Length: {manifest.Length}");

            engine.AddGas(ApplicationEngine.StoragePrice * (nefFile.Length + manifest.Length));

            NefFile nef = nefFile.AsSerializable<NefFile>();
            UInt160 hash = Helper.GetContractHash(tx.Sender, nef.Script);
            ContractState contract = engine.Snapshot.Contracts.TryGet(hash);
            if (contract != null) throw new InvalidOperationException($"Contract Already Exists: {hash}");
            contract = new ContractState
            {
                Id = GetNextAvailableId(engine.Snapshot),
                UpdateCounter = 0,
                Script = nef.Script,
                Hash = hash,
                Manifest = ContractManifest.Parse(manifest)
            };

            if (!contract.Manifest.IsValid(hash)) throw new InvalidOperationException($"Invalid Manifest Hash: {hash}");

            engine.Snapshot.Contracts.Add(hash, contract);

            // Execute _deploy

            ContractMethodDescriptor md = contract.Manifest.Abi.GetMethod("_deploy");
            if (md != null)
                engine.CallFromNativeContract(null, hash, md.Name, false);

            return contract;
        }

        [ContractMethod(0, CallFlags.AllowModifyStates)]
        private void Update(ApplicationEngine engine, byte[] nefFile, byte[] manifest)
        {
            if (nefFile is null && manifest is null) throw new ArgumentException();

            engine.AddGas(ApplicationEngine.StoragePrice * ((nefFile?.Length ?? 0) + (manifest?.Length ?? 0)));

            var contract = engine.Snapshot.Contracts.GetAndChange(engine.CallingScriptHash);
            if (contract is null) throw new InvalidOperationException($"Updating Contract Does Not Exist: {engine.CallingScriptHash}");

            if (nefFile != null)
            {
                if (nefFile.Length == 0)
                    throw new ArgumentException($"Invalid NefFile Length: {nefFile.Length}");

                NefFile nef = nefFile.AsSerializable<NefFile>();

                // Update script
                contract.Script = nef.Script;
            }
            if (manifest != null)
            {
                if (manifest.Length == 0 || manifest.Length > ContractManifest.MaxLength)
                    throw new ArgumentException($"Invalid Manifest Length: {manifest.Length}");
                contract.Manifest = ContractManifest.Parse(manifest);
                if (!contract.Manifest.IsValid(contract.Hash))
                    throw new InvalidOperationException($"Invalid Manifest Hash: {contract.Hash}");
            }
            contract.UpdateCounter++; // Increase update counter
            if (nefFile != null)
            {
                ContractMethodDescriptor md = contract.Manifest.Abi.GetMethod("_deploy");
                if (md != null)
                    engine.CallFromNativeContract(null, contract.Hash, md.Name, true);
            }
        }

        [ContractMethod(0_01000000, CallFlags.AllowModifyStates)]
        private void Destroy(ApplicationEngine engine)
        {
            UInt160 hash = engine.CallingScriptHash;
            ContractState contract = engine.Snapshot.Contracts.TryGet(hash);
            if (contract is null) return;
            engine.Snapshot.Contracts.Delete(hash);
            foreach (var (key, _) in engine.Snapshot.Storages.Find(BitConverter.GetBytes(contract.Id)))
                engine.Snapshot.Storages.Delete(key);
        }
    }
}
