namespace DeepwaterEngagementSuite.VoyagePlannerData;

public enum ModScope
{
    Adjacent,
    Voyage,
    Self,
}

public record Modifier(string Name, double Weight, ModScope Scope = ModScope.Adjacent);
