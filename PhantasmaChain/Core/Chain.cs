﻿using Phantasma.Utils;
using Phantasma.VM;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Phantasma.Core
{
    public partial class Chain
    {
        private Dictionary<byte[], Transaction> _transactions = new Dictionary<byte[], Transaction>(new ByteArrayComparer());
        private Dictionary<uint, Block> _blocks = new Dictionary<uint, Block>();
        private Dictionary<byte[], Contract> _accounts = new Dictionary<byte[], Contract>(new ByteArrayComparer());

        private Dictionary<string, Token> _tokenIDMap = new Dictionary<string, Token>();

        private Dictionary<string, Token> _tokenNameMap = new Dictionary<string, Token>();

        public Token NativeToken { get; private set; }
        public IEnumerable<Block> Blocks => _blocks.Values;

        public uint Height => (uint)_blocks.Count;
        
        public Block lastBlock { get; private set; }

        public Chain(KeyPair owner)
        {          
            var block = CreateGenesisBlock(owner);
            if (!AddBlock(block))
            {
                throw new ChainException("Genesis block failure");
            }
        }

        public bool AddBlock(Block block)
        {
            if (lastBlock != null)
            {
                if (lastBlock.Height != block.Height - 1)
                {
                    return false;
                }

                if (!block.PreviousHash.SequenceEqual(lastBlock.Hash))
                {
                    return false;
                }
            }

            foreach (var tx in block.Transactions)
            {
                if (!tx.IsValid(this))
                {
                    return false;
                }
            }

            _blocks[block.Height] = block;
            lastBlock = block;

            foreach (var tx in block.Transactions)
            {
                tx.Execute(this, block.Notify);
                _transactions[tx.Hash] = tx;
            }

            Log.Message($"Increased chain height to {block.Height}");

            return true;
        }

        private Block CreateGenesisBlock(KeyPair owner)
        {
            var script = ScriptUtils.TokenIssueScript("Phantasma","SOUL", 100000000, 100000000, Contracts.TokenAttribute.Burnable | Contracts.TokenAttribute.Tradable);
            var tx = new Transaction(owner.PublicKey, script, 0, 0);
            tx.Sign(owner);

            var block = new Block(DateTime.UtcNow.ToTimestamp(), owner.PublicKey, new Transaction[] { tx });

            return block;
        }

        public Contract GetAccount(byte[] publicKey)
        {
            if (_accounts.ContainsKey(publicKey))
            {
                return _accounts[publicKey];
            }

            return null;
        }

        public Contract GetOrCreateAccount(byte[] publicKey)
        {
            var account = GetAccount(publicKey);

            if (account == null)
            {
                account = new AccountContract(this, publicKey);
                _accounts[publicKey] = account;
            }

            return account;
        }

        public Token GetTokenByID(string symbol)
        {
            if (symbol != null && _tokenIDMap.ContainsKey(symbol))
            {
                return _tokenIDMap[symbol];
            }

            return null;
        }

        public Token GetTokenByName(string name)
        {
            if (!string.IsNullOrEmpty(name) && _tokenNameMap.ContainsKey(name))
            {
                return _tokenNameMap[name];
            }

            return null;
        }
    }
}
