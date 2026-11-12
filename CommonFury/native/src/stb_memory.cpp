#include "stb_memory.h"
#include <cstdlib>
#include <limits>
#include <new>

namespace commonfury::texture::stb_memory {

struct alignas(std::max_align_t) allocation {
    allocation* previous;
    allocation* next;
    scope* owner;
};

namespace {
thread_local scope* active_scope = nullptr;

size_t allocation_size(size_t size) {
    if (size > std::numeric_limits<size_t>::max() - sizeof(allocation))
        throw std::bad_alloc();
    return size + sizeof(allocation);
}
}

scope::scope() noexcept : previous_(active_scope) { active_scope = this; }

scope::~scope() {
    while (first_) release(first_ + 1);
    active_scope = previous_;
}

void* allocate(size_t size) {
    auto* block = static_cast<allocation*>(std::malloc(allocation_size(size)));
    if (!block) throw std::bad_alloc();
    block->previous = nullptr;
    block->owner = active_scope;
    block->next = active_scope ? active_scope->first_ : nullptr;
    if (block->next) block->next->previous = block;
    if (active_scope) active_scope->first_ = block;
    return block + 1;
}

void* reallocate(void* data, size_t size) {
    if (!data) return allocate(size);
    if (!size) {
        release(data);
        return nullptr;
    }
    auto* block = static_cast<allocation*>(data) - 1;
    auto* resized = static_cast<allocation*>(std::realloc(block, allocation_size(size)));
    if (!resized) throw std::bad_alloc();
    if (resized->previous) resized->previous->next = resized;
    else if (resized->owner) resized->owner->first_ = resized;
    if (resized->next) resized->next->previous = resized;
    return resized + 1;
}

void release(void* data) noexcept {
    if (!data) return;
    auto* block = static_cast<allocation*>(data) - 1;
    if (block->previous) block->previous->next = block->next;
    else if (block->owner) block->owner->first_ = block->next;
    if (block->next) block->next->previous = block->previous;
    std::free(block);
}

}
