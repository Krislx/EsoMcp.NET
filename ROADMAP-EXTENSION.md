# Roadmap metadata extension

This fork adds `refresh_catalog(itemIds)` for selected recipe, potion and glyph definitions. Account `inventory` queries with `includeDetails: true` report link overrides, effective traits, default/custom enchantments and known CP160 requirements without treating unknown binding as transferable. `build` exposes actual observed allocation separately from `savedBuilds`; `statistics` exposes already-parsed statistics.

Guide targets accept caller-declared `skillPointMultipliers` and `retainedSkills`. `analyze_build(section: "skillBudget")` computes a conservative complete post-respec bound; free grants may reduce it. This is not an unlock or application check. Missing definitions leave the bound unknown. The analysis does not invent CP prerequisites or active Scribing configurations absent from addon observations.

Only definition IDs leave the computer during explicit catalog refreshes. Account observations, names and learned flags remain local. `samples/EsoMcp.Smoke --config` permits protocol verification against explicit configuration.

For source builds with a sibling library in a different folder, pass `-p:UseLocalEsoData=true -p:LocalEsoDataProject=<absolute csproj path>`. The default sibling path is unchanged.

The release packages the corresponding EsoData.NET fork alongside the MCP. No game data, configuration or database is bundled. This is a fork release, not an upstream official release.
