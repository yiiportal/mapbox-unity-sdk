using System;
using System.Collections.Generic;

namespace Mapbox.BaseModule.Data.Platform.Cache.SQLiteCache
{
	// FORK yiiportal: byte-budgeted, family-aware disk eviction.
	public delegate string TilesetFamilyResolver(string tilesetId);

	public sealed class SqliteCacheBudget
	{
		public IReadOnlyDictionary<string, long> FamilyBudgetBytes { get; }
		public long DefaultFamilyBudgetBytes { get; }

		public SqliteCacheBudget(IReadOnlyDictionary<string, long> familyBudgetBytes, long defaultFamilyBudgetBytes)
		{
			FamilyBudgetBytes = familyBudgetBytes ?? throw new ArgumentNullException(nameof(familyBudgetBytes));
			DefaultFamilyBudgetBytes = defaultFamilyBudgetBytes;
		}

		public long GetBudgetBytes(string family)
		{
			return FamilyBudgetBytes.TryGetValue(family ?? string.Empty, out long budgetBytes)
				? budgetBytes
				: DefaultFamilyBudgetBytes;
		}
	}
}
