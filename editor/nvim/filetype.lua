-- Tree-sitter filetype detection for quill.
--
-- NOTE: ftdetect/ does NOT work in neovim 0.12 -- `runtime/filetype.lua` is
-- what actually runs, and it is sourced from every runtimepath directory's
-- `filetype.lua`. So this file, not an `ftdetect/quill.vim`, is the hook.

vim.filetype.add({
  extension = {
    qll = "quill",
  },
})