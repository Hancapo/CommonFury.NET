#pragma once

#include <cstddef>

namespace commonfury::texture::stb_memory {

struct allocation;

// stb is compiled as C++. Allocation failure can unwind to the C ABI guard;
// the scope frees all intermediate stb allocations even on those paths.
class scope {
public:
    scope() noexcept;
    ~scope();
    scope(const scope&) = delete;
    scope& operator=(const scope&) = delete;

private:
    allocation* first_ = nullptr;
    scope* previous_;
    friend void* allocate(size_t size);
    friend void* reallocate(void* data, size_t size);
    friend void release(void* data) noexcept;
};

void* allocate(size_t size);
void* reallocate(void* data, size_t size);
void release(void* data) noexcept;

}
