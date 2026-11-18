import axios from 'axios';
export const api=axios.create({baseURL:import.meta.env.VITE_API_URL||'http://localhost:8081/api',timeout:8000});
api.interceptors.request.use(config=>{const token=localStorage.getItem('nexstock_token');if(token)config.headers.Authorization=`Bearer ${token}`;return config;});
api.interceptors.response.use(r=>r,e=>{if(e.response?.status===401)localStorage.removeItem('nexstock_token');return Promise.reject(e);});
