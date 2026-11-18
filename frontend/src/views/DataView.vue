<template><div class="heading-row"><div><div class="eyebrow">OPERATIONS / {{label.toUpperCase()}}</div><h1>{{label}}</h1><p>Manage and keep track of your {{label.toLowerCase()}}.</p></div><button v-if="section==='products'||section==='orders'||section==='purchases'||section==='suppliers'||section==='customers'||section==='warehouses'||section==='users'" class="button primary" @click="create"><Plus :size="16"/> New {{singular}}</button></div><div v-if="error" class="error-banner"><WifiOff :size="17"/><span>{{error}}</span><button @click="load">Retry</button></div><div class="card list-card"><div class="list-toolbar"><div class="search-box"><Search :size="16"/><input v-model="search" :placeholder="`Search ${label.toLowerCase()}...`"/></div><div v-if="section==='reports'" class="range-inputs"><label>From<input type="date" v-model="reportStart" @change="load"/></label><label>To<input type="date" v-model="reportEnd" @change="load"/></label></div><button class="button secondary small" @click="applyFilter"><SlidersHorizontal :size="15"/> Filters</button><button class="button secondary small" @click="exportCsv"><Download :size="15"/> Export</button></div><div class="table-scroll"><table><thead><tr><th v-for="col in columns" :key="col">{{col.toUpperCase()}}</th><th v-if="section==='inventory'||section==='purchases'||section==='orders'||section==='notifications'||section==='suppliers'||section==='customers'||section==='warehouses'||section==='users'">ACTIONS</th></tr></thead><tbody><tr v-for="(row,i) in visibleRows" :key="row.id||i"><td v-for="col in columns" :key="col"><template v-if="col==='status'"><span class="status status-green"><i></i>{{row[col]||'Active'}}</span></template><template v-else-if="col==='read'"><span class="status" :class="row.read?'status-green':'status-amber'"><i></i>{{row.read?'Read':'Unread'}}</span></template><template v-else-if="col==='price'||col==='total'">{{fmt(row[col])}}</template><template v-else-if="col==='name'"><div class="product-cell"><div class="product-icon"><Package :size="16"/></div><b>{{row[col]||row.customer||row.supplier}}</b></div></template><template v-else>{{row[col]??'â€”'}}</template></td><td v-if="section==='suppliers'||section==='customers'||section==='warehouses'||section==='users'"><button class="row-action" @click="editRecord(row)">Edit</button> <button class="row-action danger-action" @click="deleteRecord(row)">Delete</button></td><td v-if="section==='notifications'"><button v-if="!row.read" class="row-action" @click="markRead(row)">Mark read</button><span v-else class="status status-green">Read</span></td><td v-if="section==='orders'"><button v-if="row.status!=='Delivered'&&row.status!=='Cancelled'" class="row-action" @click="updateOrder(row)">Update status</button></td><td v-if="section==='inventory'"><button class="row-action" @click="adjustStock(row)">Adjust stock</button></td><td v-if="section==='purchases'"><button v-if="row.status!=='Received'" class="row-action" @click="receive(row)">Receive</button><span v-else class="status status-green">Received</span></td></tr><tr v-if="!loading&&!filtered.length"><td :colspan="columns.length+(section==='inventory'||section==='purchases'||section==='orders'||section==='notifications'||section==='suppliers'||section==='customers'||section==='warehouses'||section==='users'?1:0)" class="empty-row"><div class="empty-symbol"><component :is="section==='products'?Package:Boxes" :size="21"/></div><b>No {{label.toLowerCase()}} found</b><span>{{search?'Try another search term.':`Create your first ${singular} to get started.`}}</span></td></tr><tr v-if="loading"><td :colspan="columns.length" class="empty-row">Loading {{label.toLowerCase()}}â€¦</td></tr></tbody></table></div><div class="pagination"><span>Showing <b>{{filtered.length?Math.min((page-1)*pageSize+1,filtered.length):0}}–{{Math.min(page*pageSize,filtered.length)}}</b> of <b>{{filtered.length}}</b> results</span><div><button :disabled="page<=1" @click="page--">Previous</button><button class="current-page">{{page}} / {{pageCount}}</button><button :disabled="page>=pageCount" @click="page++">Next</button></div></div></div></template>
<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue';
import { useRoute } from 'vue-router';
import { api } from '../services/api';
import { Plus, WifiOff, Search, SlidersHorizontal, Download, Package, Boxes } from 'lucide-vue-next';

const route = useRoute();
const section = computed(() => String(route.params.section || 'products'));
const label = computed(() => section.value.charAt(0).toUpperCase() + section.value.slice(1));
const singular = computed(() => ({ products: 'product', orders: 'order', purchases: 'purchase order' } as Record<string, string>)[section.value] || section.value.slice(0, -1));
const items = ref<Record<string, any>[]>([]);
const loading = ref(true);
const error = ref('');
const search = ref('');
const page = ref(1);
const pageSize = 10;
const today = new Date().toISOString().slice(0, 10);
const reportEnd = ref(today);
const reportStart = ref(new Date(Date.now() - 29 * 86400000).toISOString().slice(0, 10));
const fmt = (value: number) => new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(value || 0);
const columns = computed(() => {
  const map: Record<string, string[]> = {
    products: ['name', 'sku', 'category', 'stock', 'price', 'status'],
    inventory: ['name', 'sku', 'warehouse', 'stock', 'reorderLevel'],
    orders: ['number', 'customer', 'date', 'items', 'total', 'status'],
    purchases: ['number', 'supplier', 'date', 'items', 'total', 'status'],
    suppliers: ['name', 'email', 'phone', 'company', 'status'],
    customers: ['name', 'email', 'phone', 'company', 'status'],
    warehouses: ['name', 'location', 'capacity'],
    reports: ['name', 'category', 'stock', 'price'],
    users: ['name', 'email', 'role', 'status'],
    notifications: ['title', 'message', 'type', 'read']
  };
  return map[section.value] || ['name', 'status'];
});
const filtered = computed(() => items.value.filter(row => JSON.stringify(row).toLowerCase().includes(search.value.toLowerCase())));
const pageCount = computed(() => Math.max(1, Math.ceil(filtered.value.length / pageSize)));
const visibleRows = computed(() => filtered.value.slice((page.value - 1) * pageSize, page.value * pageSize));
watch(search, () => page.value = 1);

async function load() {
  page.value = 1;
  loading.value = true;
  error.value = '';
  try {
    if (section.value === 'reports' && reportStart.value > reportEnd.value) {
      error.value = 'Start date must be on or before end date.';
      items.value = [];
      return;
    }
    const { data } = await api.get('/' + section.value, section.value === 'reports'
      ? { params: { startDate: reportStart.value, endDate: reportEnd.value } }
      : undefined);
    items.value = Array.isArray(data) ? data : (data.items || []);
  } catch (e: any) {
    error.value = e.response?.data?.detail || e.response?.data?.message || `Could not load ${label.value.toLowerCase()}.`;
    items.value = [];
  } finally { loading.value = false; }
}

async function create() {
  try {
    if (section.value === 'products') {
      const name = prompt('Product name'); if (!name) return;
      const sku = prompt('SKU'); if (!sku) return;
      await api.post('/products', {
        name, sku, category: prompt('Category', 'General') || 'General',
        price: Number(prompt('Unit price', '0') || 0), stock: Number(prompt('Initial stock', '0') || 0),
        reorderLevel: Number(prompt('Reorder level', '5') || 5), warehouse: 'Main warehouse'
      });
    } else if (section.value === 'orders' || section.value === 'purchases') {
      const partner = prompt(section.value === 'orders' ? 'Customer name' : 'Supplier name'); if (!partner) return;
      const products = (await api.get('/products')).data;
      const sku = prompt('Enter a product SKU:\n' + products.map((product: any) => `${product.sku} - ${product.name}`).join('\n'));
      if (!sku) return;
      const product = products.find((candidate: any) => candidate.sku.toLowerCase() === sku.toLowerCase() || candidate.id === sku);
      if (!product) { error.value = 'No product matches that SKU.'; return; }
      const quantity = Number(prompt('Quantity', '1'));
      if (!Number.isInteger(quantity) || quantity < 1) { error.value = 'Quantity must be a whole number greater than zero.'; return; }
      await api.post('/' + section.value, section.value === 'orders'
        ? { customer: partner, items: [{ productId: product.id, quantity }] }
        : { supplier: partner, items: [{ productId: product.id, quantity }] });
    } else if (section.value === 'suppliers' || section.value === 'customers') {
      const name = prompt('Contact name'); if (!name) return;
      await api.post('/' + section.value, { name, email: prompt('Email address') || '', phone: prompt('Phone number') || '', company: prompt('Company') || '' });
    } else if (section.value === 'warehouses') {
      const name = prompt('Warehouse name'); if (!name) return;
      await api.post('/warehouses', { name, location: prompt('Location') || '', capacity: Number(prompt('Capacity', '0') || 0) });
    } else if (section.value === 'users') {
      const name = prompt('User name'); if (!name) return;
      const email = prompt('Email address'); if (!email) return;
      await api.post('/users', { name, email, role: prompt('Role: Staff, Manager or Admin', 'Staff') || 'Staff', password: prompt('Temporary password (12+ characters)') || '' });
    }
    await load();
  } catch (e: any) { error.value = e.response?.data?.detail || e.response?.data?.message || 'Could not create this record.'; }
}

async function editRecord(row: any) {
  try {
    if (section.value === 'suppliers' || section.value === 'customers') {
      await api.put('/' + section.value + '/' + row.id, {
        name: prompt('Name', row.name) || row.name, email: prompt('Email', row.email) ?? row.email,
        phone: prompt('Phone', row.phone) ?? row.phone, company: prompt('Company', row.company) ?? row.company, status: row.status || 'Active'
      });
    } else if (section.value === 'warehouses') {
      await api.put('/warehouses/' + row.id, {
        name: prompt('Name', row.name) || row.name, location: prompt('Location', row.location) ?? row.location,
        capacity: Number(prompt('Capacity', String(row.capacity)) ?? row.capacity)
      });
    } else if (section.value === 'users') {
      await api.put('/users/' + row.id, {
        name: prompt('Name', row.name) || row.name, email: prompt('Email', row.email) || row.email,
        role: prompt('Role: Staff, Manager or Admin', row.role) || row.role,
        password: prompt('New password (leave blank to keep current)') || null
      });
    }
    await load();
  } catch (e: any) { error.value = e.response?.data?.detail || e.response?.data?.message || 'Could not update this record.'; }
}

async function deleteRecord(row: any) {
  if (!confirm(`Delete ${row.name || row.email}?`)) return;
  try { await api.delete('/' + section.value + '/' + row.id); await load(); }
  catch (e: any) { error.value = e.response?.data?.detail || e.response?.data?.message || 'Could not delete this record.'; }
}

async function adjustStock(row: any) {
  const quantity = Number(prompt(`Signed stock change for ${row.name}; use a negative number to remove stock.`, '1'));
  if (!Number.isInteger(quantity) || quantity === 0) return;
  try { await api.post(`/inventory/${row.id}/adjust`, { quantity }); await load(); }
  catch (e: any) { error.value = e.response?.data?.detail || e.response?.data?.message || 'Could not adjust stock.'; }
}
async function receive(row: any) {
  if (!confirm(`Mark ${row.number} as received and add its quantities to stock?`)) return;
  try { await api.post(`/purchases/${row.id}/receive`); await load(); }
  catch (e: any) { error.value = e.response?.data?.detail || e.response?.data?.message || 'Could not receive this purchase.'; }
}
async function updateOrder(row: any) {
  const nextStatus: Record<string, string> = { Pending: 'Confirmed', Confirmed: 'Processing', Processing: 'Shipped', Shipped: 'Delivered' };
  const next = nextStatus[row.status];
  const status = prompt(`Next status: ${next || 'Cancelled'}. Enter Cancelled to cancel the order.`, next || 'Cancelled');
  if (!status) return;
  try { await api.patch(`/orders/${row.id}/status`, { status }); await load(); }
  catch (e: any) { error.value = e.response?.data?.detail || e.response?.data?.message || 'Could not update the order.'; }
}
function applyFilter() { const value = prompt('Filter by a visible value such as category, status, or warehouse.', search.value); if (value !== null) search.value = value; }
function exportCsv() {
  const rows = [columns.value, ...filtered.value.map(row => columns.value.map(column => String(row[column] ?? '')))];
  const csv = rows.map(row => row.map(value => '"' + value.replaceAll('"', '""') + '"').join(',')).join('\r\n');
  const url = URL.createObjectURL(new Blob([csv], { type: 'text/csv;charset=utf-8' }));
  const link = document.createElement('a'); link.href = url; link.download = section.value + '-export.csv'; link.click(); URL.revokeObjectURL(url);
}
async function markRead(row: any) { try { await api.patch('/notifications/' + row.id + '/read'); await load(); } catch { error.value = 'Could not update notification status.'; } }
onMounted(load);
watch(section, load);
</script>
