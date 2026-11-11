let opener;
export function showDialog(element) {
    opener = document.activeElement;
    element.showModal();
    element.querySelector('.cancel-confirmation')?.focus();
}
export function closeDialog(element) {
    if (!element.open) return;
    element.close();
    if (opener?.isConnected) opener.focus();
}
export async function copy(value) { await navigator.clipboard.writeText(value); }
