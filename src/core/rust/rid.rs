//! Generational slotmap RID allocator -- the one shared implementation behind PLAN.md's "every Rust-side object crossing the FFI boundary is an opaque u64". Instantiated per subsystem, never shared across subsystems (sim-visible RID values must not depend on render-side allocation timing).
//!
//! Ported from Godot's core/templates/rid_owner.h with deliberate deviations: per-slot generations instead of Godot's process-global counter (whose stale detection wraps program-wide at ~2^31 allocations), a plain Vec of enum slots instead of chunked storage + sentinel-value validator encodings (the chunking exists for Godot's lock-free readers, which we don't have; the sentinels collide at generation 0x7FFFFFFF), and a type tag actually stored in the RID (Godot's type safety is purely which-owner-you-ask, by convention).

/// Bit layout of a RID: low 32 bits slot index, next 8 bits allocator type tag, top 24 bits generation. Generation starts at 1 and the raw value 0 is reserved as null, so a live RID is never 0.
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub struct Rid(u64);

const INDEX_BITS: u32 = 32;
const TAG_BITS: u32 = 8;
const TAG_SHIFT: u32 = INDEX_BITS;
const GENERATION_SHIFT: u32 = INDEX_BITS + TAG_BITS;
const GENERATION_MAX: u32 = (1 << (64 - GENERATION_SHIFT)) - 1;

impl Rid {
    pub const NULL: Rid = Rid(0);

    pub fn from_raw(raw: u64) -> Rid {
        Rid(raw)
    }

    pub fn raw(self) -> u64 {
        self.0
    }

    pub fn is_null(self) -> bool {
        self.0 == 0
    }

    fn encode(index: u32, tag: u8, generation: u32) -> Rid {
        debug_assert!(
            (1..=GENERATION_MAX).contains(&generation),
            "generation {generation} out of the 24-bit range"
        );
        Rid(((generation as u64) << GENERATION_SHIFT) | ((tag as u64) << TAG_SHIFT) | index as u64)
    }

    fn index(self) -> u32 {
        self.0 as u32
    }

    fn tag(self) -> u8 {
        (self.0 >> TAG_SHIFT) as u8
    }

    fn generation(self) -> u32 {
        (self.0 >> GENERATION_SHIFT) as u32
    }
}

/// Why `remove` failures are loud errors while `get` failures are `None`: a stale lookup is a normal query the caller decides about, but a stale free is always a caller bug (double-free or cross-subsystem confusion) -- mirroring Godot's null-on-stale-lookup vs ERR_FAIL-on-stale-free split.
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum RidError {
    Null,
    TagMismatch,
    Stale,
}

impl std::fmt::Display for RidError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            RidError::Null => write!(f, "null RID"),
            RidError::TagMismatch => write!(
                f,
                "RID belongs to a different allocator (type tag mismatch)"
            ),
            RidError::Stale => write!(f, "stale RID (already freed, or never allocated here)"),
        }
    }
}

enum Slot<T> {
    // `generation` in Free is the value the next occupant will be stamped with (incremented at free time, not reuse time, so the free list never holds a retirable slot).
    Free { generation: u32 },
    Occupied { generation: u32, value: T },
    // A slot whose 24-bit generation space is exhausted; never reused, so an ancient stale RID can never alias a new object. 16M reuses per slot before this triggers.
    Retired,
}

pub struct RidAllocator<T> {
    tag: u8,
    slots: Vec<Slot<T>>,
    // LIFO free stack -- deterministic given deterministic call order, per the Determinism rules (nothing randomizes reuse).
    free: Vec<u32>,
    live: usize,
}

impl<T> RidAllocator<T> {
    pub fn new(tag: u8) -> RidAllocator<T> {
        RidAllocator {
            tag,
            slots: Vec::new(),
            free: Vec::new(),
            live: 0,
        }
    }

    pub fn insert(&mut self, value: T) -> Rid {
        // `live` increments at the end of each path, not up front: this crate's FFI layer catches panics and keeps allocators alive afterward, so invariants must survive unwinding.
        if let Some(index) = self.free.pop() {
            let Slot::Free { generation } = self.slots[index as usize] else {
                unreachable!("free list pointed at a non-free slot");
            };
            self.slots[index as usize] = Slot::Occupied { generation, value };
            self.live += 1;
            return Rid::encode(index, self.tag, generation);
        }
        let index = u32::try_from(self.slots.len()).expect("RID slot index space (2^32) exhausted");
        self.slots.push(Slot::Occupied {
            generation: 1,
            value,
        });
        self.live += 1;
        Rid::encode(index, self.tag, 1)
    }

    pub fn get(&self, rid: Rid) -> Option<&T> {
        match self.resolve(rid) {
            Ok(index) => {
                let Slot::Occupied { value, .. } = &self.slots[index] else {
                    unreachable!("resolve returned a non-occupied slot");
                };
                Some(value)
            }
            Err(_) => None,
        }
    }

    pub fn get_mut(&mut self, rid: Rid) -> Option<&mut T> {
        match self.resolve(rid) {
            Ok(index) => {
                let Slot::Occupied { value, .. } = &mut self.slots[index] else {
                    unreachable!("resolve returned a non-occupied slot");
                };
                Some(value)
            }
            Err(_) => None,
        }
    }

    pub fn remove(&mut self, rid: Rid) -> Result<T, RidError> {
        let index = self.resolve(rid)?;
        let next_generation = rid.generation() + 1;
        let replacement = if next_generation > GENERATION_MAX {
            Slot::Retired
        } else {
            self.free.push(index as u32);
            Slot::Free {
                generation: next_generation,
            }
        };
        let Slot::Occupied { value, .. } = std::mem::replace(&mut self.slots[index], replacement)
        else {
            unreachable!("resolve returned a non-occupied slot");
        };
        self.live -= 1;
        Ok(value)
    }

    pub fn len(&self) -> usize {
        self.live
    }

    pub fn is_empty(&self) -> bool {
        self.live == 0
    }

    // Every still-live RID, in index order. The shutdown leak report; also usable by future engine-destroy assertions.
    pub fn leaks(&self) -> Vec<Rid> {
        let mut leaked = Vec::new();
        for (index, slot) in self.slots.iter().enumerate() {
            if let Slot::Occupied { generation, .. } = slot {
                leaked.push(Rid::encode(index as u32, self.tag, *generation));
            }
        }
        leaked
    }

    // The one validity check behind both lookup and removal: Ok(index into slots) only when the RID is this allocator's tag and the slot is occupied at exactly the RID's generation.
    fn resolve(&self, rid: Rid) -> Result<usize, RidError> {
        if rid.is_null() {
            return Err(RidError::Null);
        }
        if rid.tag() != self.tag {
            return Err(RidError::TagMismatch);
        }
        let index = rid.index() as usize;
        match self.slots.get(index) {
            Some(Slot::Occupied { generation, .. }) if *generation == rid.generation() => Ok(index),
            _ => Err(RidError::Stale),
        }
    }
}

// Shutdown leak report, Godot's "%d RID allocations were leaked at exit" in spirit: raw IDs only for now, richer attribution (type names, allocation sites) later. Straight to stderr because this fires during teardown -- the buffered log pipeline may already be gone, and a leak report that can itself be leaked is no report at all.
impl<T> Drop for RidAllocator<T> {
    fn drop(&mut self) {
        let leaked = self.leaks();
        if !leaked.is_empty() {
            eprintln!(
                "RidAllocator(tag {}) dropped with {} live RID(s) leaked: {}",
                self.tag,
                leaked.len(),
                leaked
                    .iter()
                    .map(|rid| format!("{:#x}", rid.raw()))
                    .collect::<Vec<_>>()
                    .join(", ")
            );
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn insert_get_roundtrip() {
        let mut allocator = RidAllocator::new(7);
        let rid = allocator.insert("hello");
        assert!(!rid.is_null());
        assert_eq!(allocator.get(rid), Some(&"hello"));
        assert_eq!(allocator.len(), 1);
        assert_eq!(rid.tag(), 7);
        assert_eq!(rid.index(), 0);
        assert_eq!(rid.generation(), 1);
    }

    #[test]
    fn get_mut_mutates() {
        let mut allocator = RidAllocator::new(1);
        let rid = allocator.insert(10);
        *allocator.get_mut(rid).unwrap() += 5;
        assert_eq!(allocator.get(rid), Some(&15));
    }

    #[test]
    fn remove_returns_value_and_stales_the_rid() {
        let mut allocator = RidAllocator::new(1);
        let rid = allocator.insert(42);
        assert_eq!(allocator.remove(rid), Ok(42));
        assert_eq!(allocator.get(rid), None);
        assert_eq!(allocator.len(), 0);
    }

    #[test]
    fn reuse_gets_new_generation_and_stale_rid_never_aliases() {
        let mut allocator = RidAllocator::new(1);
        let old = allocator.insert("old");
        allocator.remove(old).unwrap();
        let new = allocator.insert("new");
        assert_eq!(new.index(), old.index());
        assert_ne!(new.generation(), old.generation());
        assert_eq!(allocator.get(old), None);
        assert_eq!(allocator.get(new), Some(&"new"));
    }

    #[test]
    fn double_free_is_a_loud_error() {
        let mut allocator = RidAllocator::new(1);
        let rid = allocator.insert(1);
        allocator.remove(rid).unwrap();
        assert_eq!(allocator.remove(rid), Err(RidError::Stale));
    }

    #[test]
    fn tag_mismatch_rejected() {
        let mut apples = RidAllocator::new(1);
        let mut oranges: RidAllocator<i32> = RidAllocator::new(2);
        let apple = apples.insert(1);
        assert_eq!(oranges.get(apple), None);
        assert_eq!(oranges.remove(apple), Err(RidError::TagMismatch));
        // Same index and generation exist in oranges; only the tag differs -- the exact confusion the tag exists to reject.
        let orange = oranges.insert(2);
        assert_eq!(orange.index(), apple.index());
        assert_eq!(orange.generation(), apple.generation());
        assert_eq!(oranges.get(apple), None);
    }

    #[test]
    fn null_rid_rejected() {
        let mut allocator: RidAllocator<i32> = RidAllocator::new(1);
        assert_eq!(allocator.get(Rid::NULL), None);
        assert_eq!(allocator.remove(Rid::NULL), Err(RidError::Null));
    }

    #[test]
    fn out_of_range_index_is_stale_not_panic() {
        let mut allocator: RidAllocator<i32> = RidAllocator::new(1);
        let bogus = Rid::encode(999, 1, 1);
        assert_eq!(allocator.get(bogus), None);
        assert_eq!(allocator.remove(bogus), Err(RidError::Stale));
    }

    #[test]
    fn free_list_is_lifo_and_order_pinned() {
        let mut allocator = RidAllocator::new(1);
        let a = allocator.insert("a");
        let b = allocator.insert("b");
        let c = allocator.insert("c");
        assert_eq!((a.index(), b.index(), c.index()), (0, 1, 2));
        allocator.remove(a).unwrap();
        allocator.remove(c).unwrap();
        // Freed 0 then 2; LIFO reuse must hand back 2 first, then 0. This exact order is part of the determinism contract -- if it changes, that is a breaking determinism event, not a refactor detail.
        let first = allocator.insert("first");
        let second = allocator.insert("second");
        assert_eq!(first.index(), 2);
        assert_eq!(second.index(), 0);
        let third = allocator.insert("third");
        assert_eq!(third.index(), 3);
    }

    #[test]
    fn generation_exhaustion_retires_the_slot() {
        let mut allocator = RidAllocator::new(1);
        let rid = allocator.insert("about to retire");
        // In-module test surgery: age the slot to the last usable generation instead of cycling it 16M times.
        allocator.slots[0] = Slot::Occupied {
            generation: GENERATION_MAX,
            value: "about to retire",
        };
        let aged = Rid::encode(0, 1, GENERATION_MAX);
        assert_eq!(allocator.get(aged), Some(&"about to retire"));
        assert_eq!(allocator.get(rid), None);
        allocator.remove(aged).unwrap();
        // The retire branch is the one remove path that skips the free-list push -- pin that it still keeps the live count right.
        assert_eq!(allocator.len(), 0);
        // The slot is retired, not freed: the next insert must use a fresh index, and the aged RID stays dead forever.
        let next = allocator.insert("fresh");
        assert_eq!(next.index(), 1);
        assert_eq!(allocator.len(), 1);
        assert_eq!(allocator.get(aged), None);
        assert_eq!(allocator.remove(aged), Err(RidError::Stale));
    }

    #[test]
    fn leaks_lists_live_rids_in_index_order() {
        let mut allocator = RidAllocator::new(1);
        let a = allocator.insert("a");
        let b = allocator.insert("b");
        let c = allocator.insert("c");
        allocator.remove(b).unwrap();
        assert_eq!(allocator.leaks(), vec![a, c]);
        allocator.remove(a).unwrap();
        allocator.remove(c).unwrap();
        assert_eq!(allocator.leaks(), Vec::new());
    }

    #[test]
    fn raw_roundtrip_is_the_ffi_contract() {
        let mut allocator = RidAllocator::new(3);
        let rid = allocator.insert("crossing");
        assert_eq!(Rid::from_raw(rid.raw()), rid);
        assert_eq!(allocator.get(Rid::from_raw(rid.raw())), Some(&"crossing"));
        assert_eq!(Rid::NULL.raw(), 0);
    }
}
