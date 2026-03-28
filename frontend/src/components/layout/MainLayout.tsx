import { Outlet, Navigate } from 'react-router-dom'
import { Layout, Menu } from 'antd'
import { BookOutlined, UserOutlined, LogoutOutlined } from '@ant-design/icons'
import { useAuthStore } from '@/stores/authStore'
import styles from './MainLayout.module.css'

const { Header, Sider, Content } = Layout

function MainLayout() {
  const { user, logout, isAuthenticated } = useAuthStore()

  if (!isAuthenticated) {
    return <Navigate to="/login" replace />
  }

  const menuItems = [
    {
      key: '/novels',
      icon: <BookOutlined />,
      label: '小说管理',
    },
    {
      key: '/characters',
      icon: <UserOutlined />,
      label: '角色管理',
    },
  ]

  return (
    <Layout className={styles.layout}>
      <Sider width={200} className={styles.sider}>
        <div className={styles.logo}>
          <BookOutlined className={styles.logoIcon} />
          <span>AI小说生成系统</span>
        </div>
        <Menu
          mode="inline"
          defaultSelectedKeys={['/novels']}
          items={menuItems}
          className={styles.menu}
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
