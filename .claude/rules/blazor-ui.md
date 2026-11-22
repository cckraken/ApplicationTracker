---
paths:
  - "src/ApplicationTracker.App/**"
---

# Blazor UI rules

`Components/Pages/ApplicationDetail.razor` is the reference implementation of these patterns (`ContactList.razor` follows it). Copy its approach rather than inventing a new one.

## Page patterns

- Interactive Server is set globally on `Routes` in `App.razor`, so pages don't need their own `@rendermode`.
- Create a `DbContext` per operation through `IDbContextFactory` (the `Core` services already do); never hold one for the life of a component or circuit.
- `OnInitializedAsync` can run twice (prerendering, then the interactive render), so it should only load data: no side effects that break or repeat when it runs again.
- Catch, log and keep the current state in event handlers and load methods: an unhandled exception ends the circuit and the page stops responding.
- Serialize or version async work that quick repeated clicks or typing can overlap. Wrap the whole handler, including its reload or reset, so a late second click is ignored; a handler whose form has already closed must return without doing anything.
- Turn a `ValidationException` from a `Core` service into a field error on the form.
- Restore focus after a re-render by checking the data, not just an `@ref` dictionary: entries outlive the elements they pointed to.
- Render the page's one `<h1>` in every state (loading, not found, loaded), so `FocusOnNavigate` always finds it.
- Live-region announcements must be re-read even when the text repeats: use an `Announce` helper like `ApplicationDetail.razor`'s, not a direct assignment.

## Styling

- Colours and other design values are `--at-*` tokens in `wwwroot/app.css` (light and dark). Add new tokens there; never hard-code colours in components.
- Reuse `Components/Shared/` components and the shared classes in `app.css` before creating new ones. Styles for one component go in its `Component.razor.css`.
- Status is never shown by colour alone: always colour plus a text label.

## Gotchas

- A `string` component parameter given a bare name is a literal: `Error="contactError"` passes the text "contactError"; write `Error="@contactError"`. Non-string parameters (bool, lists) are evaluated as C# without `@`.
- Razor attribute values can't contain double-quoted strings or tuple keys (`@ref="dict[(id, "x")]"` doesn't compile); use constants or separate dictionaries.
- Focus styles use `outline: 2px solid transparent`, never `outline: none`, so they still show in forced-colours mode.
- Wide content relies on `main { min-width: 0 }` (`Components/Layout/MainLayout.razor.css`); a scroll container with absolutely positioned children (e.g. `.visually-hidden` text) needs `position: relative` (see the "Pipeline board" section of `app.css`).