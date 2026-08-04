namespace xpTURN.Klotho.ECS
{
    public readonly struct StorageLayout
    {
        public readonly int TypeId;
        public readonly int Capacity;       // entity-index domain = Sparse length = Has bound (= maxEntities)
        public readonly int SlotCapacity;   // concurrent slots = Dense/Components length (singleton 1, else maxEntities)
        public readonly int CountOffset;
        public readonly int SparseOffset;
        public readonly int DenseOffset;
        public readonly int ComponentsOffset;
        public readonly int ComponentSize;
        public readonly int TotalSize;

        public StorageLayout(int typeId, int capacity, int slotCapacity,
                             int countOffset, int sparseOffset,
                             int denseOffset, int componentsOffset,
                             int componentSize, int totalSize)
        {
            TypeId = typeId;
            Capacity = capacity;
            SlotCapacity = slotCapacity;
            CountOffset = countOffset;
            SparseOffset = sparseOffset;
            DenseOffset = denseOffset;
            ComponentsOffset = componentsOffset;
            ComponentSize = componentSize;
            TotalSize = totalSize;
        }
    }
}
