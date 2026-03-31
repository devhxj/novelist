import { Outlet, Navigate, useNavigate, useLocation } from 'react-router-dom'
import { Layout, Menu } from 'antd'
import { BookOutlined, LogoutOutlined } from '@ant-design/icons'
import { useAuthStore } from '@/stores/authStore'
import styles from './MainLayout.module.css'

const { Header, Sider, Content } = Layout

function MainLayout() {
  const { user, logout, isAuthenticated } = useAuthStore()
  const navigate = useNavigate()
  const location = useLocation()

  if (!isAuthenticated) {
    return <Navigate to="/login" replace />
  }

  const isEditorPage = location.pathname.includes('/editor')

  const menuItems = [
    {
      key: '/novels',
      icon: <BookOutlined />,
      label: '小说管理',
    },
  ]

  const getSelectedKey = () => {
    const path = location.pathname
    if (path === '/novels' || path === '/novels/create') {
      return '/novels'
    }
    if (path.startsWith('/novels/')) {
      return '/novels'
    }
    return path
  }

  if (isEditorPage) {
    return (
      <Layout className={styles.layoutFullscreen}>
        <Outlet />
      </Layout>
    )
  }

  return (
    <Layout className={styles.layout}>
      <Sider width={200} className={styles.sider}>
        <div className={styles.logo}>
          <BookOutlined className={styles.logoIcon} />
          <span>AI小说生成系统</span>
        </div>
        <Menu
          mode="inline"
          selectedKeys={[getSelectedKey()]}
          items={menuItems}
          className={styles.menu}
          onClick={({ key }) => {
            navigate(key)
          }}
        />
      </Sider>
      <Layout>
        <Header className={styles.header}>
          <div className={styles.headerRight}>
            <span className={styles.username}>{user?.username}</span>
            <LogoutOutlined className={styles.logoutIcon} onClick={logout} />
          </div>
        </Header>
        <Content className={styles.content}>
          <Outlet />
        </Content>
      </Layout>
    </Layout>
  )
}

export default MainLayout
