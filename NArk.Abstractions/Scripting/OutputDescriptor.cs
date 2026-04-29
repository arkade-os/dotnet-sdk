using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using NBitcoin.DataEncoders;
#if HAS_SPAN
using NBitcoin.Secp256k1;
#endif

#nullable enable

namespace NBitcoin.Scripting
{

	public abstract class OutputDescriptor : IEquatable<OutputDescriptor>
	{
		private OutputDescriptor(Network network)
		{
			Network = network;
		}
		public Network Network { get; }
		#region subtypes
		public class Addr : OutputDescriptor
		{
			public IDestination Address { get; }
			public Addr(IDestination address, Network network) : base(network)
			{
				if (address == null)
					throw new ArgumentNullException(nameof(address));
				Address = address;
			}
		}

		public class Raw : OutputDescriptor
		{
			public Script Script;

			internal Raw(Script script, Network network) : base(network)
			{
				if (script is null)
					throw new ArgumentNullException(nameof(script));
				if (script.Length == 0)
					throw new ArgumentException($"{nameof(script)} must not be empty!");
				Script = script;
			}
		}

		public class PK : OutputDescriptor
		{
			public PubKeyProvider PkProvider;
			internal PK(PubKeyProvider pkProvider, Network network) : base(network)
			{
				if (pkProvider == null)
					throw new ArgumentNullException(nameof(pkProvider));
				PkProvider = pkProvider;
			}
		}

		public class PKH : OutputDescriptor
		{
			public PubKeyProvider PkProvider;
			internal PKH(PubKeyProvider pkProvider, Network network) : base(network)
			{
				if (pkProvider == null)
					throw new ArgumentNullException(nameof(pkProvider));
				PkProvider = pkProvider;
			}
		}

		public class WPKH : OutputDescriptor
		{
			public PubKeyProvider PkProvider;
			internal WPKH(PubKeyProvider pkProvider, Network network) : base(network)
			{
				if (pkProvider == null)
					throw new ArgumentNullException(nameof(pkProvider));
				PkProvider = pkProvider;
			}
		}
		public class Combo : OutputDescriptor
		{
			public PubKeyProvider PkProvider;
			internal Combo(PubKeyProvider pkProvider, Network network) : base(network)
			{
				if (pkProvider == null)
					throw new ArgumentNullException(nameof(pkProvider));
				PkProvider = pkProvider;
			}
		}

		public class Multi : OutputDescriptor
		{
			public List<PubKeyProvider> PkProviders;
			internal Multi(uint threshold, IEnumerable<PubKeyProvider> pkProviders, bool isSorted, Network network, bool isTapScript) : base(network)
			{
				if (pkProviders == null)
					throw new ArgumentNullException(nameof(pkProviders));
				PkProviders = pkProviders.ToList();
				if (PkProviders.Count == 0)
					throw new ArgumentException("Multisig Descriptor can not have empty pubkey providers");
				Threshold = threshold;
				IsSorted = isSorted;
				IsTapScript = isTapScript;
			}

			public uint Threshold { get; }
			public bool IsSorted { get; }
			public bool IsTapScript { get; }
		}

		public class SH : OutputDescriptor
		{
			public OutputDescriptor Inner;
			internal SH(OutputDescriptor inner, Network network) : base(network)
			{
				if (inner == null)
					throw new ArgumentNullException(nameof(inner));
				if (inner.IsTopLevelOnly())
					throw new ArgumentException($"{inner} can not be inner element for SH");
				Inner = inner;
			}
		}

		public class WSH : OutputDescriptor
		{
			public OutputDescriptor Inner;
			internal WSH(OutputDescriptor inner, Network network) : base(network)
			{
				if (inner == null)
					throw new ArgumentNullException(nameof(inner));
				if (inner.IsTopLevelOnly() || inner is WSH)
					throw new ArgumentException($"{inner} can not be inner element for WSH");
				Inner = inner;
			}
		}

#if HAS_SPAN
		/// <summary>
		/// binary tree to represent MAST in Taproot script for descriptor.
		/// </summary>
		public abstract class TapTree
		{
			public class Leaf : TapTree
			{
				public OutputDescriptor Inner { get; }

				internal Leaf(OutputDescriptor inner)
				{
					Inner = inner;
				}
			}

			public class Tree : TapTree
			{
				public TapTree Child1 { get; }
				public TapTree Child2 { get; }

				internal Tree(TapTree child1, TapTree child2)
				{
					Child1 = child1;
					Child2 = child2;
				}
			}

			public static TapTree NewLeaf(OutputDescriptor inner) => new Leaf(inner);
			public static TapTree NewTree(TapTree child1, TapTree child2) => new Tree(child1, child2);

			public override string ToString() => this switch
			{
				Tree self =>
					$"{{{self.Child1},{self.Child2}}}",
				Leaf self =>
					$"{self.Inner.ToStringHelper()}", // we don't need checksum here so do not use `ToString`
				_ => throw new Exception($"Unreachable type {GetType()}")
			};



			private IEnumerable<(OutputDescriptor, int)> IterateScriptsCore(int depth)
			{
				switch (this)
				{
					case Leaf self:
						yield return (self.Inner, depth);
						break;
					case Tree self:
						foreach (var child1Result in self.Child1.IterateScriptsCore(depth + 1))
							yield return child1Result;
						foreach (var child2Result in self.Child2.IterateScriptsCore(depth + 1))
							yield return child2Result;
						break;
				}
			}

			/// <summary>
			/// Iterates all tap script with its depth.
			/// Depth-first.
			/// </summary>
			/// <returns></returns>
			public IEnumerable<(OutputDescriptor, int)> IterateScripts() => IterateScriptsCore(0);

			public static TapTree FromScriptDepths(IList<OutputDescriptor> scripts, IList<int> depths)
			{
				if (scripts == null) throw new ArgumentNullException(nameof(scripts));
				if (depths == null) throw new ArgumentNullException(nameof(depths));
				if (scripts.Count == 0)
					throw new ArgumentException(nameof(scripts));
				if (scripts.Count != depths.Count)
					throw new ArgumentException($"{nameof(scripts)} and {nameof(depths)} must have same length");

				TapTree? tree = null;
				int prevDepth = -1;
				foreach (var (sc, depth) in
				         scripts
					         .Zip(depths, (sc, dep) => (sc, dep))
					         .OrderByDescending<ValueTuple<OutputDescriptor, int>, int>(i => i.Item2))
				{
					if (prevDepth == depth && tree is not null)
					{
						tree = TapTree.NewTree(tree, NewLeaf(sc));
						prevDepth = depth - 1;
					}
					else
					{
						tree = NewLeaf(sc);
						prevDepth = depth;
					}
				}
				if (prevDepth != 0)
					throw new InvalidDataException($"Malformed script and depths. the top most depth was: {prevDepth}");
				return tree!;
			}
		}

		public class Tr : OutputDescriptor
		{
			public PubKeyProvider InnerPubkey;

			public TapTree? TapLeafs;

			public bool IsKeyPathSpendOnly => TapLeafs is null;

			internal Tr(PubKeyProvider innerPubkey, Network network, TapTree? tapLeafs) : base(network)
			{
				InnerPubkey = innerPubkey ?? throw new ArgumentNullException(nameof(innerPubkey));
				TapLeafs = tapLeafs;
			}

			/// <summary>
			/// Get TaprootSpendInfo in case InnerPubKey is not ranged.
			/// </summary>
			/// <param name="taprootSpendInfo"></param>
			/// <returns></returns>
			internal bool TryGetSpendInfo(ISigningRepository repo, [MaybeNullWhen(false)] out TaprootSpendInfo taprootSpendInfo)
			{
				Debug.Assert(!IsRange());
				taprootSpendInfo = null;
				var internalKey = InnerPubkey.GetPubKey(0, _ => null)!.TaprootInternalKey;
				var builder = new TaprootBuilder();
				if (TapLeafs is not null)
				{
					foreach (var (desc, depth) in this.TapLeafs.IterateScripts())
					{
						if (!desc.TryExpand(0, _ => null, repo, out var scripts, true))
							throw new Exception($"Failed to expand descriptor {desc}. This should never happen");

						foreach (var s in scripts)
						{
							builder.AddLeaf((uint)depth, s.ToTapScript(TapLeafVersion.C0));
						}
					}
				}
				taprootSpendInfo = builder.Finalize(internalKey);
				return true;
			}
		}

		public class RawTr : OutputDescriptor
		{
			public PubKeyProvider OutputPubKeyProvider;
			internal RawTr(PubKeyProvider outputPubKeyProvider, Network network) : base(network)
			{
				OutputPubKeyProvider = outputPubKeyProvider ?? throw new ArgumentNullException(nameof(outputPubKeyProvider));
			}
		}
#endif

		public static OutputDescriptor NewAddr(IDestination dest, Network network) => new Addr(dest, network);
		public static OutputDescriptor NewRaw(Script sc, Network network) => new Raw(sc, network);
		public static OutputDescriptor NewPK(PubKeyProvider pk, Network network) => new PK(pk, network);
		public static OutputDescriptor NewPKH(PubKeyProvider pk, Network network) => new PKH(pk, network);
		public static OutputDescriptor NewWPKH(PubKeyProvider pk, Network network) => new WPKH(pk, network);
		public static OutputDescriptor NewCombo(PubKeyProvider pk, Network network) => new Combo(pk, network);
		public static OutputDescriptor NewMulti(uint m, IEnumerable<PubKeyProvider> pks, bool isSorted, Network network, bool isTapScript = false)
			=> new Multi(m, pks, isSorted, network, isTapScript);
		public static OutputDescriptor NewSH(OutputDescriptor inner, Network network) => new SH(inner, network);
		public static OutputDescriptor NewWSH(OutputDescriptor inner, Network network) => new WSH(inner, network);
#if HAS_SPAN
		public static OutputDescriptor NewTr(PubKeyProvider innerPubKey, Network network, TapTree? tapTree = null) =>
			new Tr(innerPubKey, network, tapTree);
		public static OutputDescriptor NewRawTr(PubKeyProvider outputPubkeyProvider, Network network) =>
			new RawTr(outputPubkeyProvider, network);
#endif

		public bool IsTopLevelOnly() => this switch
		{
			Addr _ => true,
			Raw _ => true,
			Combo _ => true,
			SH _ => true,
#if HAS_SPAN
			Tr _ => true,
			RawTr _ => true,
#endif
			_ => false
		};

		#endregion

		#region Descriptor specific things

		/// <summary>
		/// Expand descriptor into actual scriptPubKeys.
		/// </summary>
		/// <param name="pos">position index to expand</param>
		/// <param name="privateKeyProvider">provider to inject private keys in case of hardened derivation</param>
		/// <param name="repo">repository to which to put resulted information.</param>
		/// <param name="outputScripts">resulted scriptPubKey</param>
		/// <returns></returns>
		public bool TryExpand(
			uint pos,
			ISigningRepository repo,
			[MaybeNullWhen(false)] out List<Script> outputScripts,
			IDictionary<uint, ExtPubKey>? cache = null
			)
		{
			return TryExpand(pos, repo.GetPrivateKey, repo, out outputScripts, false, cache);
		}


		/// <summary>
		/// Expand descriptor into actual scriptPubKeys.
		/// TODO: cache
		/// </summary>
		/// <param name="pos">position index to expand</param>
		/// <param name="privateKeyProvider">provider to inject private keys in case of hardened derivation</param>
		/// <param name="repo">repository to which to put resulted information.</param>
		/// <param name="outputScripts">resulted scriptPubKey</param>
		/// <returns></returns>
		public bool TryExpand(
			uint pos,
			Func<KeyId, Key?> privateKeyProvider,
			ISigningRepository repo,
			[MaybeNullWhen(false)] out List<Script> outputScripts,
			bool isTaproot = false,
			IDictionary<uint, ExtPubKey>? cache = null
			)
		{
			if (privateKeyProvider == null) throw new ArgumentNullException(nameof(privateKeyProvider));
			if (repo == null) throw new ArgumentNullException(nameof(repo));
			outputScripts = new List<Script>();
			return TryExpand(pos, privateKeyProvider, repo, outputScripts, isTaproot, cache);
		}

		private bool ExpandPkHelper(
			PubKeyProvider pkP,
			Func<KeyId, Key?> privateKeyProvider,
			uint pos,
			ISigningRepository repo,
			List<Script> outSc,
			bool isTaproot,
			IDictionary<uint, ExtPubKey>? cache = null)
		{
			if (!pkP.TryGetPubKey(pos, privateKeyProvider, out var keyOrigin1, out var pubkey1))
				return false;
			if (keyOrigin1 != null)
			{
				repo.SetKeyOrigin(pubkey1.Hash, keyOrigin1);
#if HAS_SPAN
				repo.SetKeyOrigin(pubkey1.GetTaprootPubKey(), keyOrigin1);
#endif
			}

			repo.SetPubKey(pubkey1.Hash, pubkey1);
			outSc.AddRange(MakeScripts(pubkey1, repo, isTaproot));
			return true;
		}

		private bool TryExpand(
			uint pos,
			Func<KeyId, Key?> privateKeyProvider,
			ISigningRepository repo,
			List<Script> outputScripts,
			bool isTaproot,
			IDictionary<uint, ExtPubKey>? cache = null
			)
		{
			switch (this)
			{
				case Addr _:
					return false;
				case Raw _:
					return false;
				case PK self:
					return ExpandPkHelper(self.PkProvider, privateKeyProvider, pos, repo, outputScripts, isTaproot);
				case PKH self:
					return ExpandPkHelper(self.PkProvider, privateKeyProvider, pos, repo, outputScripts, isTaproot);
				case WPKH self:
					return ExpandPkHelper(self.PkProvider, privateKeyProvider, pos, repo, outputScripts, isTaproot);
				case Combo self:
					return ExpandPkHelper(self.PkProvider, privateKeyProvider, pos, repo, outputScripts, isTaproot);
				case Multi self:
					// prepare temporary objects so that it won't affect the result in case
					// it fails in the middle.
					var tmpRepo = new FlatSigningRepository();
					var keys = new PubKey[self.PkProviders.Count];
					for (int i = 0; i < self.PkProviders.Count; ++i)
					{
						var pkP = self.PkProviders[i];
						if (!pkP.TryGetPubKey(pos, privateKeyProvider, out var keyOrigin1, out var pubkey1))
							return false;
						if (keyOrigin1 != null)
							tmpRepo.SetKeyOrigin(pubkey1.Hash, keyOrigin1);
						tmpRepo.SetPubKey(pubkey1.Hash, pubkey1);
						keys[i] = pubkey1;
					}

					if (self.IsSorted)
					{
						keys = keys.OrderBy(x => x).ToArray();
					}
					repo.Merge(tmpRepo);
					outputScripts.Add(PayToMultiSigTemplate.Instance.GenerateScriptPubKey((int)self.Threshold, sort: false, forceSmallSigCount: false, keys));
					return true;
				case SH self:
					var subRepo1 = new FlatSigningRepository();
					if (!self.Inner.TryExpand(pos, privateKeyProvider, subRepo1, out var shInnerResult, false))
						return false;
					repo.Merge(subRepo1);
					foreach (var inner in shInnerResult)
					{
						repo.SetScript(inner.Hash, inner);
						outputScripts.Add(inner.Hash.ScriptPubKey);
					}
					return true;
				case WSH self:
					var subRepo2 = new FlatSigningRepository();
					if (!self.Inner.TryExpand(pos, privateKeyProvider, subRepo2, out var wshInnerResult, false))
						return false;
					repo.Merge(subRepo2);
					foreach (var inner in wshInnerResult)
					{
						repo.SetScript(inner.Hash, inner);
						repo.SetScript(inner.WitHash.HashForLookUp, inner);
						outputScripts.Add(inner.WitHash.ScriptPubKey);
					}
					return true;
#if HAS_SPAN
				case Tr self:
					if (!self.InnerPubkey.TryGetPubKey(pos, privateKeyProvider, out var keyOrigin2, out var pubkey2))
						return false;
					if (keyOrigin2 != null)
						repo.SetKeyOrigin(pubkey2.GetTaprootPubKey(), keyOrigin2);
					var builder = new TaprootBuilder();
					if (self.TapLeafs is not null)
					{
						foreach (var (od, depth) in self.TapLeafs.IterateScripts())
						{
							if (!od.TryExpand(pos, privateKeyProvider, repo, out var subScripts , true))
								return false;
							foreach (var s in subScripts)
							{
								builder.AddLeaf((uint)depth, s.ToTapScript(TapLeafVersion.C0));
							}
						}
					}
					var spendInfo = builder.Finalize(pubkey2.TaprootInternalKey);
					repo.SetTaprootSpendInfo(spendInfo.OutputPubKey, spendInfo);
					outputScripts.Add(spendInfo.OutputPubKey.OutputKey.ScriptPubKey);
					return true;
				case RawTr self:
					return ExpandPkHelper(self.OutputPubKeyProvider, privateKeyProvider, pos, repo, outputScripts, true);
#endif
			}
			throw new Exception("Unreachable");
		}

		/// <summary>
		///  Make output scirptpubkey from expanded pubkey.
		/// </summary>
		/// <param name="key"></param>
		/// <param name="repo"></param>
		/// <returns></returns>
		/// <exception cref="Exception"></exception>
		private List<Script> MakeScripts(PubKey key, ISigningRepository repo, bool isTaproot)
		{
			switch (this)
			{
				case Addr self:
					return new List<Script> { self.Address.ScriptPubKey };
				case Raw self:
					return new List<Script> { self.Script };
				case PK _:
					return
#if HAS_SPAN
						isTaproot ?
						new List<Script>
						{
							new Script(
								Op.GetPushOp(key.GetTaprootPubKey().ToBytes()),
								OpcodeType.OP_CHECKSIG
							)
						} :
#endif
						new List<Script> { key.ScriptPubKey };
				case PKH _:
					return new List<Script> { key.Hash.ScriptPubKey };
				case WPKH _:
					return new List<Script> { key.WitHash.ScriptPubKey };
				case Combo _:
					var res = new List<Script>
					{
						key.ScriptPubKey,
						key.Hash.ScriptPubKey,
					};
					if (key.IsCompressed)
					{
						res.Add(key.WitHash.ScriptPubKey);
						res.Add(key.WitHash.ScriptPubKey.Hash.ScriptPubKey);
						repo.SetScript(key.WitHash.ScriptPubKey.Hash, key.WitHash.ScriptPubKey);
					}
					return res;
#if HAS_SPAN
				case RawTr _:
					return new List<Script> { key.GetTaprootPubKey().ScriptPubKey };
#endif
				// Other cases never calls this function. Because this method is just a helper for expanding above cases
			}

			throw new Exception("Unreachable");
		}

		/// <summary>
		/// Output descriptor has `solvability` property.
		/// Which means whether we are able to know how to create ScirptSig (or witness)
		/// for the descriptor.
		/// It is always false for `addr()` and `raw()`, and otherwise true.
		/// But this may change in the future, see: https://github.com/bitcoin/bitcoin/issues/24114
		/// for the discussion.
		/// </summary>
		/// <returns></returns>
		public bool IsSolvable() => this switch
		{
			Addr _ => false,
			Raw _ => false,
			SH self =>
				self.Inner.IsSolvable(),
			WSH self =>
				self.Inner.IsSolvable(),
			_ =>
				true,
		};

		public bool IsRange() => this switch
		{
			Addr _ =>
				false,
			Raw _ =>
				false,
			PK self =>
				self.PkProvider.IsRange(),
			PKH self =>
				self.PkProvider.IsRange(),
			WPKH self =>
				self.PkProvider.IsRange(),
			Combo self =>
				self.PkProvider.IsRange(),
			Multi self =>
				self.PkProviders.Any(pk => pk.IsRange()),
			SH self =>
				self.Inner.IsRange(),
			WSH self =>
				self.Inner.IsRange(),
#if HAS_SPAN
			Tr self =>
				self.InnerPubkey.IsRange() ||
				(self.TapLeafs is not null && self.TapLeafs.IterateScripts().Any(leaf => leaf.Item1.IsRange())),
			RawTr self =>
				self.OutputPubKeyProvider.IsRange(),
#endif
			_ =>
				throw new Exception("Unreachable"),
		};

		public enum ScriptContext
		{
			TOP,
			P2SH,
			P2WSH,
#if HAS_SPAN
			P2TR,
#endif
		}

		private static PubKeyProvider InferPubKey(PubKey pk, ISigningRepository repo)
		{
			var keyProvider = PubKeyProvider.NewConst(pk);
			return
				repo.TryGetKeyOrigin(pk.Hash, out var keyOrigin)
				? PubKeyProvider.NewOrigin(keyOrigin, keyProvider)
				: keyProvider;
		}

		private ScriptPubKeyType? InferTemplate(ScriptTemplate? template) => template switch
		{
			PayToPubkeyHashTemplate _ => ScriptPubKeyType.Legacy,
			PayToPubkeyTemplate _ => ScriptPubKeyType.Legacy,
			PayToWitTemplate _ => ScriptPubKeyType.Segwit,
			// in the case of p2sh, we don't know if it is p2sh or p2sh-p2[wsh|wpkh], so just return null
			_ => null
		};

		/// <summary>
		/// Infer the address type for that descriptor.
		/// When it is impossible, just return null.
		/// e.g. In case of descriptors those are agnostic to the actual scriptpubkey format (e.g. "multi"),
		/// it just returns null.
		/// </summary>
		/// <returns></returns>
		public ScriptPubKeyType? GetScriptPubKeyType() => this switch
		{
			Addr self =>
				InferTemplate(self.Address.ScriptPubKey.FindTemplate()),
			Raw self =>
				InferTemplate(self.Script.FindTemplate()),
			PK _ => null,
			PKH _ => ScriptPubKeyType.Legacy,
			WPKH _ => ScriptPubKeyType.Segwit,
			SH self =>
				self.Inner.GetScriptPubKeyType() switch
				{
					ScriptPubKeyType.Segwit => ScriptPubKeyType.SegwitP2SH,
					_ => ScriptPubKeyType.Legacy,
				},
			WSH _ => ScriptPubKeyType.Segwit,
#if HAS_SPAN
			Tr self =>
				self.TapLeafs is null ?
				ScriptPubKeyType.TaprootBIP86 :
				null,
#endif
			_ => null
		};


		private string ToStringHelper() => this switch
		{
			Addr self =>
				$"addr({self.Address})",
			Raw self =>
				$"raw({self.Script.ToHex()})",
			PK self =>
				$"pk({self.PkProvider})",
			PKH self =>
				$"pkh({self.PkProvider})",
			WPKH self =>
				$"wpkh({self.PkProvider})",
			Combo self =>
				$"combo({self.PkProvider})",
			Multi self =>
				$"{(self.IsSorted ? "sortedmulti" : "multi")}{(self.IsTapScript ? "_a" : "")}({self.Threshold},{String.Join(",", self.PkProviders)})",
			SH self =>
				$"sh({self.Inner.ToStringHelper()})",
			WSH self =>
				$"wsh({self.Inner.ToStringHelper()})",
#if HAS_SPAN
			Tr self =>
				self.IsKeyPathSpendOnly ?
				$"tr({self.InnerPubkey})" :
				$"tr({self.InnerPubkey},{self.TapLeafs})",
			RawTr self =>
				$"rawtr({self.OutputPubKeyProvider})",
#endif
			_ =>
				throw new Exception("unreachable")
		};

		public override string ToString()
		{
			var inner = ToStringHelper();
			return $"{inner}#{GetCheckSum(inner)}";
		}

		/// <summary>
		/// Parse descriptor from string representation.
		/// OutputDescriptor class does not hold private key data in memory, so if you want to parse
		/// private key, you must pass the reference to the DB with `repo` argument.
		/// Parser will inject private keys they've found into the DB. this can later be used with other methods
		/// such as `TryGetPrivateString`
		/// </summary>
		/// <param name="desc">descriptor to parse</param>
		/// <param name="network">Network for the descriptor.</param>
		/// <param name="requireCheckSum">if true, Do not parse descriptors without checksum. default: false</param>
		/// <param name="repo">repository to inject private key information.</param>
		/// <returns></returns>
		public static OutputDescriptor Parse(string desc, Network network, bool requireCheckSum = false, ISigningRepository? repo = null)
			=> OutputDescriptorParser.ParseOD(desc, network, requireCheckSum, repo);

		/// <summary>
		/// Parse descriptor from string representation.
		/// OutputDescriptor class does not hold private key data in memory, so if you want to parse
		/// private key, you must pass the reference to the DB with `repo` argument.
		/// Parser will inject private keys they've found into the DB. this can later be used with other methods
		/// such as `TryGetPrivateString`
		/// </summary>
		/// <param name="desc">descriptor to parse</param>
		/// <param name="network">Network for the descriptor.</param>
		/// <param name="requireCheckSum">If true, Do not parse descriptors without checksum. Default: false</param>
		/// <param name="repo">repository to inject private key information.</param>
		/// <returns></returns>
		public static bool TryParse(string desc, Network network, out OutputDescriptor? result, bool requireCheckSum = false, ISigningRepository? repo = null)
			=> OutputDescriptorParser.TryParseOD(desc, network, out result, requireCheckSum, repo);

		#endregion

		#region Equatable

		public sealed override bool Equals(object? obj)
			=> Equals(obj as OutputDescriptor);

		public bool Equals(OutputDescriptor? other) => (other != null) && (this) switch
		{
			Addr self =>
				other is Addr o && self.Address.Equals(o.Address),
			Raw self =>
				other is Raw o && self.Script.Equals(o.Script),
			PK self =>
				other is PK o && self.PkProvider.Equals(o.PkProvider),
			PKH self =>
				other is PKH o && self.PkProvider.Equals(o.PkProvider),
			WPKH self =>
				other is WPKH o && self.PkProvider.Equals(o.PkProvider),
			Combo self =>
				other is Combo o && self.PkProvider.Equals(o.PkProvider),
			Multi self =>
				other is Multi o &&
				self.Threshold == o.Threshold &&
				self.PkProviders.SequenceEqual(o.PkProviders) &&
				self.IsSorted == o.IsSorted,
			SH self =>
				other is SH o && self.Inner.Equals(o.Inner),
			WSH self =>
				other is WSH o && self.Inner.Equals(o.Inner),
#if HAS_SPAN
			Tr self =>
				other is Tr o && self.InnerPubkey.Equals(o.InnerPubkey) &&
					((self.TapLeafs is null && o.TapLeafs is null) || self.TapLeafs?.ToString().Equals(o.TapLeafs?.ToString()) == true),
			RawTr self =>
				other is RawTr o && self.OutputPubKeyProvider.Equals(o.OutputPubKeyProvider),
#endif
			_ =>
				throw new Exception("Unreachable!"),
		};

		public override int GetHashCode()
		{
			int num;
			switch (this)
			{
				case Addr self:
					{
						num = 0;
						return -1640531527 + self.Address.GetHashCode() + ((num << 6) + (num >> 2));
					}
				case Raw self:
					{
						num = 1;
						return -1640531527 + self.Script.GetHashCode() + ((num << 6) + (num >> 2));
					}
				case PK self:
					{
						num = 2;
						return -1640531527 + self.PkProvider.GetHashCode() + ((num << 6) + (num >> 2));
					}
				case PKH self:
					{
						num = 3;
						return -1640531527 + self.PkProvider.GetHashCode() + ((num << 6) + (num >> 2));
					}
				case WPKH self:
					{
						num = 4;
						return -1640531527 + self.PkProvider.GetHashCode() + ((num << 6) + (num >> 2));
					}
				case Combo self:
					{
						num = 5;
						return -1640531527 + self.PkProvider.GetHashCode() + ((num << 6) + (num >> 2));
					}
				case Multi self:
					{
						num = 6;
						num = self.Threshold.GetHashCode() + ((num << 6) + (num >> 2));
						num = self.IsSorted.GetHashCode() + ((num << 6) + (num >> 2));
						foreach (var pk in self.PkProviders)
						{
							num = -1640531527 + pk.GetHashCode() + ((num << 6) + (num >> 2));
						}
						return num;
					}
				case SH self:
					{
						num = 7;
						return -1640531527 + self.Inner.GetHashCode() + ((num << 6) + (num >> 2));
					}
				case WSH self:
					{
						num = 8;
						return -1640531527 + self.Inner.GetHashCode() + ((num << 6) + (num >> 2));
					}
#if HAS_SPAN
				case Tr self:
					{
						num = 9;
						num = -1640531527 + self.InnerPubkey.GetHashCode() + ((num << 6) + (num >> 2));
						var iter = self.TapLeafs?.IterateScripts();
						if (iter is null)
							return num;
						foreach (var i in iter)
							num = -1640531527 + i.GetHashCode() + ((num << 6) + (num >> 2));
						return num;
					}
				case RawTr self:
					{
						num = 10;
						return -1640531527 + self.OutputPubKeyProvider.GetHashCode() + ((num << 6) + (num >> 2));
					}
#endif
			default:
					throw new Exception("Unreachable!");
			}
		}

		#endregion

		#region checksum
		/** The character set for the checksum itself (same as bech32). */
		static readonly char[] CHECKSUM_CHARSET = "qpzry9x8gf2tvdw0s3jn54khce6mua7l".ToCharArray();
		static readonly string INPUT_CHARSET_STRING =
		"0123456789()[],'/*abcdefgh@:$%{}" +
        "IJKLMNOPQRSTUVWXYZ&+-.;<=>?!^_|~" +
        "ijklmnopqrstuvwxyzABCDEFGH`#\"\\ ";

		static readonly char[] INPUT_CHARSET = INPUT_CHARSET_STRING.ToCharArray();

		public static string AddChecksum(string desc) => $"{desc}#{GetCheckSum(desc)}";
		public static string GetCheckSum(string desc)
		{
			if (desc is null)
				throw new ArgumentNullException(nameof(desc));
			ulong c = 1;
			int cls = 0;
			int clscount = 0;
			foreach(var ch in desc.ToCharArray())
			{
				var pos = INPUT_CHARSET_STRING.IndexOf(ch);
				if (pos == -1)
					return "";
				c = PolyMod(c, pos & 31);
				cls = cls * 3 + (pos >> 5);
				if (++clscount == 3)
				{
					c = PolyMod(c, cls);
					cls = 0;
					clscount = 0;
				}
			}
			if (clscount > 0) c = PolyMod(c, cls);
			for (int j = 0; j < 8; ++j) c = PolyMod(c, 0);
			c ^= 1;
			var result = new char[8];
			for (int j = 0; j < 8; ++j)
			{
				result[j] = CHECKSUM_CHARSET[(c >> (5 * (7 - j))) & 31];
			}
			return new String(result);
		}
		static ulong PolyMod(ulong c, int val)
		{
			ulong c0 = c >> 35;
			c = ((c & 0x7ffffffffUL) << 5) ^ (ulong)val;
			if ((c0 & 1UL) != 0) c ^= 0xf5dee51989;
			if ((c0 & 2UL) != 0) c ^= 0xa9fdca3312;
			if ((c0 & 4UL) != 0) c ^= 0x1bab10e32d;
			if ((c0 & 8) != 0) c ^= 0x3706b1677a;
			if ((c0 & 16) != 0) c ^= 0x644d626ffd;
			return c;
		}

		#endregion
	}
}
#nullable disable
